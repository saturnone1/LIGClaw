using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Sidecar;

public sealed class SidecarSupervisor : IConversationSidecar, IFeatureBuilderRuntimeClient, IAsyncDisposable
{
    private static readonly IReadOnlyList<string> RequiredCapabilities = Array.AsReadOnly(
    [
        "health.ping",
        "conversation.start",
        "conversation.cancel",
        "agent.events",
        "provider.configure",
        "provider.test",
        "mcp.configure",
        "mcp.status",
        "mcp.call",
        "tool.invoke",
        "tool.result",
        "capability.runtime.snapshot.v2",
        "capability.proposal.generate",
        "workflow.invoke",
        "workflow.result",
        "workflow.runtime.v1",
        "runtime.cline.0.0.65",
    ]);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableRunThreshold = TimeSpan.FromSeconds(30);
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
    public event EventHandler<ToolInvokeParams>? ToolInvocationReceived;
    public event EventHandler<WorkflowInvokeParams>? WorkflowInvocationReceived;

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
        string runId,
        string input,
        string runtime,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? history = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object?>? providerRouting = null,
        LIGClaw.Contracts.Generated.CapabilityRuntimeSnapshot? capabilitySnapshot = null) =>
        GetConnectedClient().InvokeAsync<ConversationStartResult>(
            "conversation.start",
            new ConversationStartParams(
                conversationId,
                runId,
                input,
                runtime,
                history,
                providerRouting,
                capabilitySnapshot),
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

    public Task<McpConfigureResult> ConfigureMcpAsync(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> connections,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<McpConfigureResult>(
            "mcp.configure",
            new McpConfigureParams(connections),
            cancellationToken);

    public Task<McpStatusResult> GetMcpStatusAsync(CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<McpStatusResult>("mcp.status", new McpStatusParams(), cancellationToken);

    public Task<McpCallResult> CallMcpAsync(
        McpCallParams parameters,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<McpCallResult>("mcp.call", parameters, cancellationToken);

    public Task<ToolResultResult> SubmitToolResultAsync(
        ToolResultParams result,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<ToolResultResult>("tool.result", result, cancellationToken);

    public Task<WorkflowResultResult> SubmitWorkflowResultAsync(
        WorkflowResultParams result,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<WorkflowResultResult>("workflow.result", result, cancellationToken);

    public Task<CapabilityProposalGenerateResult> GenerateCapabilityProposalAsync(
        CapabilityProposalGenerateParams request,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().InvokeAsync<CapabilityProposalGenerateResult>(
            "capability.proposal.generate",
            request,
            cancellationToken);

    public async Task<FeatureBuilderRuntimeResult> GenerateAsync(
        FeatureBuilderRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await GenerateCapabilityProposalAsync(
            new CapabilityProposalGenerateParams(
                request.SchemaVersion,
                request.GenerationId,
                request.RequestHash.Value,
                request.SessionId,
                request.SessionRevision,
                request.InventoryHash.Value,
                request.DeadlineUtc,
                request.Requirement,
                request.Answers.Select(answer => new CapabilityProposalAnswer(
                    answer.QuestionId,
                    answer.Value)).ToArray(),
                new CapabilityProposalInventory(
                    1,
                    request.Inventory.Tools,
                    request.Inventory.ConnectionKinds,
                    request.Inventory.CapabilityIds)),
            cancellationToken).ConfigureAwait(false);

        return new FeatureBuilderRuntimeResult(
            checked((int)result.SchemaVersion),
            result.GenerationId,
            CapabilityContentHash.Parse(result.RequestHash),
            result.SessionId,
            result.SessionRevision,
            CapabilityContentHash.Parse(result.InventoryHash),
            result.DeadlineUtc,
            result.Outcome switch
            {
                "needs_information" => FeatureBuilderRuntimeOutcome.NeedsInformation,
                "proposal" => FeatureBuilderRuntimeOutcome.Proposal,
                "refused" => FeatureBuilderRuntimeOutcome.Refused,
                _ => throw new InvalidDataException("Sidecar returned an unsupported Capability proposal outcome."),
            },
            result.Questions?.Select(question => new FeatureBuilderQuestion(
                question.QuestionId,
                question.Prompt,
                question.AnswerKind switch
                {
                    "text" => FeatureBuilderAnswerKind.Text,
                    "single_choice" => FeatureBuilderAnswerKind.SingleChoice,
                    _ => throw new InvalidDataException("Sidecar returned an unsupported Feature Builder answer kind."),
                },
                question.Choices ?? [])).ToArray(),
            result.Artifacts?.Select(artifact => new FeatureBuilderRawProposalArtifact(
                artifact.Path,
                artifact.MediaType,
                artifact.Content)).ToArray(),
            result.Limitations?.ToArray(),
            result.RefusalCode);
    }

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
                PublishDiagnostic($"Sidecar attempt failed: {exception.GetType().Name}");
                if (consecutiveFailures >= 5)
                {
                    Publish(SidecarState.Faulted, "Sidecar entered crash-loop protection. Retrying slowly.");
                }
                else
                {
                    Publish(SidecarState.Restarting, "Agent sidecar disconnected. Restarting.");
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
        var connected = false;

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken)
                .WaitAsync(ConnectionTimeout, cancellationToken)
                .ConfigureAwait(false);

            await using var client = new RpcClient(pipe);
            client.NotificationReceived += HandleNotification;
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
            connected = true;
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
            if (connected && !cancellationToken.IsCancellationRequested)
                Publish(SidecarState.Restarting, "Agent sidecar disconnected. Restarting.");

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

    internal void HandleNotification(object? sender, RpcNotification notification)
    {
        if (notification.Params is null) return;
        try
        {
            if (StringComparer.Ordinal.Equals(notification.Method, "tool.invoke"))
            {
                var invocation = notification.Params.Value.Deserialize<ToolInvokeParams>(ContentLengthMessageStream.SerializerOptions)
                    ?? throw new InvalidDataException("Tool invocation was empty.");
                if (string.IsNullOrWhiteSpace(invocation.ToolCallId) ||
                    string.IsNullOrWhiteSpace(invocation.ConversationId) ||
                    string.IsNullOrWhiteSpace(invocation.RunId) ||
                    string.IsNullOrWhiteSpace(invocation.Name) ||
                    invocation.Risk is not ("R0" or "R1" or "R2" or "R3" or "R4"))
                    throw new InvalidDataException("Tool invocation fields were invalid.");
                ToolInvocationReceived?.Invoke(this, invocation);
                return;
            }
            if (StringComparer.Ordinal.Equals(notification.Method, "workflow.invoke"))
            {
                var invocation = notification.Params.Value.Deserialize<WorkflowInvokeParams>(
                    ContentLengthMessageStream.SerializerOptions)
                    ?? throw new InvalidDataException("Workflow invocation was empty.");
                if (!IsExactIdentifier(invocation.WorkflowCallId, "wfinv_", 48) ||
                    !IsExactIdentifier(invocation.IdempotencyKey, "wfid_", 64) ||
                    string.IsNullOrWhiteSpace(invocation.ConversationId) ||
                    string.IsNullOrWhiteSpace(invocation.RunId) ||
                    !StringComparer.Ordinal.Equals(invocation.Name, "workflow.invoke.v1") ||
                    invocation.CapabilityContext is null || invocation.Input is null)
                    throw new InvalidDataException("Workflow invocation fields were invalid.");
                WorkflowInvocationReceived?.Invoke(this, invocation);
                return;
            }
            if (!StringComparer.Ordinal.Equals(notification.Method, "agent.event")) return;
            var agentEvent = notification.Params.Value.Deserialize<AgentEvent>(ContentLengthMessageStream.SerializerOptions)
                ?? throw new InvalidDataException("Agent event was empty.");
            if (string.IsNullOrWhiteSpace(agentEvent.ConversationId) ||
                string.IsNullOrWhiteSpace(agentEvent.RunId) ||
                agentEvent.Sequence < 0 ||
                agentEvent.Type is not ("run_started" or "routing_changed" or "text_delta" or "run_completed" or "run_cancelled" or "run_failed"))
                throw new InvalidDataException("Agent event fields were invalid.");
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
            PublishDiagnostic($"Ignored invalid Sidecar notification: {exception.GetType().Name}");
        }
    }

    private Process StartSidecar(string pipeName, string sessionToken)
    {
        var sidecarPath = ResolveSidecarPath();
        var bundledNode = Path.Combine(AppContext.BaseDirectory, "sidecar", "node.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("LIGCLAW_NODE_PATH") ??
                (File.Exists(bundledNode) ? bundledNode : "node"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(sidecarPath)!,
        };
        startInfo.ArgumentList.Add(sidecarPath);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment["LIGCLAW_SESSION_TOKEN"] = sessionToken;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the agent sidecar.");
    }

    private static string ResolveSidecarPath()
    {
        var configured = Environment.GetEnvironmentVariable("LIGCLAW_SIDECAR_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var packagedCandidate = Path.Combine(AppContext.BaseDirectory, "sidecar", "dist", "index.js");
        if (File.Exists(packagedCandidate)) return packagedCandidate;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "sidecar", "dist", "index.js");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Sidecar build was not found. Run scripts/verify.ps1 first.");
    }

    internal static void ValidateHandshake(InitializeResult result)
    {
        if (!StringComparer.Ordinal.Equals(result.ProtocolVersion, ProtocolConstants.ProtocolVersion))
            throw new InvalidDataException($"Unsupported sidecar protocol version '{result.ProtocolVersion}'.");
        if (!StringComparer.Ordinal.Equals(result.ContractHash, ProtocolConstants.ContractHash))
            throw new InvalidDataException("Desktop and sidecar contract hashes do not match.");

        var advertisedCapabilities = new HashSet<string>(result.Capabilities ?? [], StringComparer.Ordinal);
        var missingCapabilities = RequiredCapabilities
            .Where(capability => !advertisedCapabilities.Contains(capability))
            .ToArray();
        if (missingCapabilities.Length > 0)
        {
            throw new InvalidDataException(
                $"Sidecar is missing required capabilities: {string.Join(", ", missingCapabilities)}.");
        }
    }

    private static bool IsExactIdentifier(string value, string prefix, int hexadecimalLength) =>
        value is not null && value.Length == prefix.Length + hexadecimalLength &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef") == false;

    private async Task DrainAsync(StreamReader reader, string source, CancellationToken cancellationToken)
    {
        var reportedSuppression = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;
            if (reportedSuppression) continue;
            reportedSuppression = true;
            PublishDiagnostic($"{source}: output suppressed by diagnostic content policy.");
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
