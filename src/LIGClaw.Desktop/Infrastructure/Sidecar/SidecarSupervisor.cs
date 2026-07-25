using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;

namespace LIGClaw.Desktop.Infrastructure.Sidecar;

public sealed class SidecarSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableRunThreshold = TimeSpan.FromSeconds(30);
    private const int MaximumDiagnosticLength = 2_048;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, long> _eventSequences = new();
    private readonly object _sync = new();
    private Task? _supervisionTask;
    private Process? _process;
    private RpcClient? _client;
    private int _restartRequested;

    public event EventHandler<SidecarStatus>? StatusChanged;
    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<AgentEvent>? AgentEventReceived;

    public void Start()
    {
        lock (_sync)
        {
            _supervisionTask ??= Task.Run(() => SuperviseAsync(_lifetime.Token));
        }
    }

    public void RequestRestart()
    {
        Interlocked.Exchange(ref _restartRequested, 1);
        Process? process;
        lock (_sync) process = _process;
        TryTerminate(process);
    }

    public Task<ConversationStartResult> StartConversationAsync(
        string conversationId,
        string input,
        string runtime,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<ConversationStartResult>(
            "conversation.start",
            new ConversationStartParams(conversationId, input, runtime),
            cancellationToken);

    public Task<ConversationCancelResult> CancelConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<ConversationCancelResult>(
            "conversation.cancel",
            new ConversationCancelParams(conversationId),
            cancellationToken);

    public Task<ProviderConfigureResult> ConfigureProviderAsync(
        ProviderConfigureParams configuration,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<ProviderConfigureResult>("provider.configure", configuration, cancellationToken);

    public Task<ProviderTestResult> TestProviderAsync(
        ProviderConfigureParams configuration,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<ProviderTestResult>("provider.test", configuration, cancellationToken);

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var attemptStartedAt = Stopwatch.GetTimestamp();
            Publish(consecutiveFailures == 0 ? SidecarState.Starting : SidecarState.Restarting,
                consecutiveFailures == 0 ? "Starting agent sidecar…" : "Restarting agent sidecar…");

            try
            {
                await RunOneInstanceAsync(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var wasRequested = Volatile.Read(ref _restartRequested) == 1;
                if (Stopwatch.GetElapsedTime(attemptStartedAt) >= StableRunThreshold)
                {
                    consecutiveFailures = 0;
                }
                if (!wasRequested)
                {
                    consecutiveFailures++;
                }
                PublishDiagnostic($"Sidecar attempt failed: {exception.Message}");
                if (consecutiveFailures >= 5)
                {
                    Publish(SidecarState.Faulted, "Sidecar entered crash-loop protection. Retrying slowly.");
                }
            }

            if (cancellationToken.IsCancellationRequested) break;
            var requested = Interlocked.Exchange(ref _restartRequested, 0) == 1;
            var delay = requested ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(consecutiveFailures, 5));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        Publish(SidecarState.Stopped, "Agent sidecar stopped.");
    }

    private async Task RunOneInstanceAsync(CancellationToken cancellationToken)
    {
        var pipeName = $"ligclaw-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var process = StartSidecar(pipeName, sessionToken);
        lock (_sync) _process = process;

        var stdout = DrainAsync(process.StandardOutput, "sidecar", cancellationToken);
        var stderr = DrainAsync(process.StandardError, "sidecar error", cancellationToken);

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken)
                .WaitAsync(ConnectionTimeout, cancellationToken)
                .ConfigureAwait(false);

            await using var client = new RpcClient(pipe);
            client.NotificationReceived += Client_NotificationReceived;
            var hostVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
            var initialized = await client.InvokeAsync<InitializeResult>(
                "initialize",
                new InitializeParams(
                    ProtocolConstants.ProtocolVersion,
                    hostVersion,
                    ProtocolConstants.ContractHash,
                    sessionToken),
                cancellationToken).ConfigureAwait(false);

            ValidateHandshake(initialized);
            lock (_sync) _client = client;
            Publish(SidecarState.Connected, $"Agent sidecar {initialized.SidecarVersion} connected.");

            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(HeartbeatTimeout);
                _ = await client.InvokeAsync<PingResult>("ping", null, heartbeat.Token).ConfigureAwait(false);
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"Sidecar exited unexpectedly with code {process.ExitCode}.");
            }
        }
        finally
        {
            _eventSequences.Clear();
            lock (_sync)
            {
                if (ReferenceEquals(_process, process)) _process = null;
                _client = null;
            }

            TryTerminate(process);
            await Task.WhenAll(IgnoreCancellation(stdout), IgnoreCancellation(stderr)).ConfigureAwait(false);
        }
    }

    private RpcClient GetConnectedClient()
    {
        lock (_sync)
        {
            return _client ?? throw new InvalidOperationException("Agent sidecar is not connected.");
        }
    }

    private void Client_NotificationReceived(object? sender, RpcNotification notification)
    {
        if (!StringComparer.Ordinal.Equals(notification.Method, "agent.event") || notification.Params is null) return;
        try
        {
            var agentEvent = notification.Params.Value.Deserialize<AgentEvent>(ContentLengthMessageStream.SerializerOptions)
                ?? throw new InvalidDataException("Agent event was empty.");
            var expected = _eventSequences.TryGetValue(agentEvent.RunId, out var last) ? last + 1 : 0;
            if (agentEvent.Sequence != expected)
            {
                PublishDiagnostic($"Ignored out-of-order agent event for run {agentEvent.RunId}: expected {expected}, received {agentEvent.Sequence}.");
                return;
            }
            _eventSequences[agentEvent.RunId] = agentEvent.Sequence;
            if (agentEvent.Type is "run_completed" or "run_cancelled" or "run_failed")
                _eventSequences.TryRemove(agentEvent.RunId, out _);
            AgentEventReceived?.Invoke(this, agentEvent);
        }
        catch (Exception exception)
        {
            PublishDiagnostic($"Ignored invalid agent event: {exception.Message}");
        }
    }

    private Process StartSidecar(string pipeName, string sessionToken)
    {
        var sidecarPath = ResolveSidecarPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("LIGCLAW_NODE_PATH") ?? "node",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(sidecarPath)!,
        };
        startInfo.ArgumentList.Add(sidecarPath);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.Environment["LIGCLAW_SESSION_TOKEN"] = sessionToken;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the agent sidecar.");
    }

    private static string ResolveSidecarPath()
    {
        var configured = Environment.GetEnvironmentVariable("LIGCLAW_SIDECAR_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "sidecar", "dist", "index.js");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Sidecar build was not found. Run scripts/verify.ps1 first.");
    }

    private static void ValidateHandshake(InitializeResult result)
    {
        if (!StringComparer.Ordinal.Equals(result.ProtocolVersion, ProtocolConstants.ProtocolVersion))
            throw new InvalidDataException($"Unsupported sidecar protocol version '{result.ProtocolVersion}'.");
        if (!StringComparer.Ordinal.Equals(result.ContractHash, ProtocolConstants.ContractHash))
            throw new InvalidDataException("Desktop and sidecar contract hashes do not match.");
    }

    private async Task DrainAsync(StreamReader reader, string source, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;
            var safeLine = line.Length <= MaximumDiagnosticLength
                ? line
                : string.Concat(line.AsSpan(0, MaximumDiagnosticLength), "… [truncated]");
            PublishDiagnostic($"{source}: {safeLine}");
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static void TryTerminate(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private void Publish(SidecarState state, string message) =>
        StatusChanged?.Invoke(this, new SidecarStatus(state, message, DateTimeOffset.UtcNow));

    private void PublishDiagnostic(string message) => DiagnosticMessage?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Process? process;
        Task? supervision;
        lock (_sync)
        {
            process = _process;
            supervision = _supervisionTask;
        }

        TryTerminate(process);
        if (supervision is not null) await IgnoreCancellation(supervision).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
