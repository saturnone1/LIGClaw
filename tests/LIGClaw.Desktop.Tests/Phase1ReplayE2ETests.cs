using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class Phase1ReplayE2ETests
{
    [Fact(Timeout = 30_000)]
    public async Task QuickAccessNaturalLanguageApprovalLaunchAndStreamingCompleteEndToEnd()
    {
        var shortcut = QuickAccessShortcutCatalog.Default;
        Assert.Equal("Ctrl + Alt + Space", shortcut.DisplayName);

        var repositoryRoot = FindRepositoryRoot();
        var sidecarPath = Path.Combine(repositoryRoot, "sidecar", "dist", "index.js");
        Assert.True(File.Exists(sidecarPath), "Build the Sidecar before running the Phase 1 replay E2E test.");
        var pipeName = $"ligclaw-phase1-e2e-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = StartSidecar(sidecarPath, pipeName, sessionToken);

        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var client = new RpcClient(pipe);
            var conversationId = "quick-access-conversation";
            var runId = "quick-access-run";
            var policy = new ToolInvocationPolicy();
            policy.BeginRun(conversationId, runId);
            var app = new RegisteredAppEntry("fixture-calculator", "Calculator", "C:\\Start Menu\\Calculator.lnk");
            var launcher = new FakeRegisteredAppLauncher();
            var host = new WindowsToolHost(
                WindowsPlatformProfile.Classify(10, 0, 19045, isWorkstation: true),
                [new AppLaunchTool(new FakeRegisteredAppCatalog(app), launcher)]);
            var approvalPrompts = new ConcurrentQueue<WindowsToolApprovalPrompt>();
            var coordinator = new ToolInvocationCoordinator(
                policy,
                host,
                (prompt, _) =>
                {
                    Assert.Empty(launcher.Launched);
                    approvalPrompts.Enqueue(prompt);
                    return Task.FromResult(true);
                });
            var events = new ConcurrentQueue<AgentEvent>();
            var terminal = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var toolHandled = new TaskCompletionSource<WindowsToolExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.NotificationReceived += (_, notification) =>
            {
                if (notification.Params is null) return;
                if (notification.Method == "tool.invoke")
                {
                    var invocation = notification.Params.Value.Deserialize<ToolInvokeParams>(ContentLengthMessageStream.SerializerOptions)!;
                    _ = HandleToolAsync(client, coordinator, invocation, toolHandled);
                    return;
                }
                if (notification.Method != "agent.event") return;
                var agentEvent = notification.Params.Value.Deserialize<AgentEvent>(ContentLengthMessageStream.SerializerOptions)!;
                events.Enqueue(agentEvent);
                if (agentEvent.Type is "run_completed" or "run_cancelled" or "run_failed")
                    terminal.TrySetResult(agentEvent);
            };

            _ = await client.InvokeAsync<InitializeResult>(
                "initialize",
                new InitializeParams(
                    ProtocolConstants.ProtocolVersion,
                    "phase1-e2e",
                    ProtocolConstants.ContractHash,
                    sessionToken));
            var started = await client.InvokeAsync<ConversationStartResult>(
                "conversation.start",
                new ConversationStartParams(conversationId, runId, "계산기 열어 줘", "cline", null, null));

            Assert.True(started.Accepted);
            Assert.Equal("run_completed", (await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type);
            var toolExecution = await toolHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(toolExecution.Success, toolExecution.Error);
            var prompt = Assert.Single(approvalPrompts);
            Assert.Equal("앱을 실행할까요?", prompt.Heading);
            Assert.Contains("Calculator 앱을 엽니다", prompt.Action, StringComparison.Ordinal);
            Assert.Equal(app, Assert.Single(launcher.Launched));
            Assert.Equal(
                "Calculator 앱을 실행했습니다.",
                string.Concat(events.Where(item => item.Type == "text_delta").Select(item => item.Text)));
            policy.EndRun(conversationId, runId);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task HandleToolAsync(
        RpcClient client,
        ToolInvocationCoordinator coordinator,
        ToolInvokeParams invocation,
        TaskCompletionSource<WindowsToolExecutionResult> handled)
    {
        try
        {
            var execution = await coordinator.ExecuteAsync(invocation);
            var result = await client.InvokeAsync<ToolResultResult>(
                "tool.result",
                new ToolResultParams(invocation.ToolCallId, execution.Success, execution.Output, execution.Error));
            Assert.True(result.Accepted);
            handled.TrySetResult(execution);
        }
        catch (Exception exception)
        {
            handled.TrySetException(exception);
        }
    }

    private static Process StartSidecar(string sidecarPath, string pipeName, string sessionToken)
    {
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
        startInfo.Environment["LIGCLAW_TEST_DETERMINISTIC"] = "1";
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Sidecar.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LIGClaw.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class FakeRegisteredAppCatalog(RegisteredAppEntry app) : IRegisteredAppCatalog
    {
        public RegisteredAppEntry? Resolve(string appName) =>
            StringComparer.OrdinalIgnoreCase.Equals(app.DisplayName, appName) ? app : null;
    }

    private sealed class FakeRegisteredAppLauncher : IRegisteredAppLauncher
    {
        public List<RegisteredAppEntry> Launched { get; } = [];

        public Task LaunchAsync(RegisteredAppEntry app, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launched.Add(app);
            return Task.CompletedTask;
        }
    }
}
