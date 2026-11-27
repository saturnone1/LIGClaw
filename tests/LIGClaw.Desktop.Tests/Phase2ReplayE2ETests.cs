using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class Phase2ReplayE2ETests
{
    [Fact(Timeout = 30_000)]
    public async Task NaturalLanguageR2ApprovalExecutionAuditAndStreamingCompleteEndToEnd()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sidecarPath = Path.Combine(repositoryRoot, "sidecar", "dist", "index.js");
        Assert.True(File.Exists(sidecarPath), "Build the Sidecar before running the Phase 2 replay E2E test.");
        var pipeName = $"ligclaw-phase2-e2e-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = StartSidecar(sidecarPath, pipeName, sessionToken);

        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var client = new RpcClient(pipe);
            const string conversationId = "phase2-file-conversation";
            const string runId = "phase2-file-run";
            var policy = new ToolInvocationPolicy();
            policy.BeginRun(conversationId, runId);
            var tool = new FakeFileMoveTool();
            var audit = new FakeAuditSink();
            var prompts = new ConcurrentQueue<WindowsToolApprovalPrompt>();
            var coordinator = new ToolInvocationCoordinator(
                policy,
                new WindowsToolHost(
                    WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true),
                    [tool]),
                (prompt, _) =>
                {
                    Assert.Equal(0, tool.ExecutionCount);
                    prompts.Enqueue(prompt);
                    return Task.FromResult(true);
                },
                audit);
            var events = new ConcurrentQueue<AgentEvent>();
            var terminal = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handled = new TaskCompletionSource<WindowsToolExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.NotificationReceived += (_, notification) =>
            {
                if (notification.Params is null) return;
                if (notification.Method == "tool.invoke")
                {
                    var invocation = notification.Params.Value.Deserialize<ToolInvokeParams>(ContentLengthMessageStream.SerializerOptions)!;
                    _ = HandleToolAsync(client, coordinator, invocation, handled);
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
                new InitializeParams(ProtocolConstants.ProtocolVersion, "phase2-e2e", ProtocolConstants.ContractHash, sessionToken));
            var started = await client.InvokeAsync<ConversationStartResult>(
                "conversation.start",
                new ConversationStartParams(conversationId, runId, "보고서 파일을 보관 폴더로 옮겨 줘", "cline", null, null));

            Assert.True(started.Accepted);
            Assert.Equal("run_completed", (await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type);
            Assert.True((await handled.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.Equal(1, tool.ExecutionCount);
            Assert.Contains("파일 1개", Assert.Single(prompts).Action, StringComparison.Ordinal);
            Assert.True(Assert.Single(audit.Approvals).Approved);
            Assert.Equal("succeeded", Assert.Single(audit.Executions).Status);
            Assert.Equal(
                "파일 1개를 이동했습니다.",
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

    private sealed class FakeFileMoveTool : IWindowsToolAdapter
    {
        public int ExecutionCount { get; private set; }
        public string Name => "file.move.v1";
        public string Risk => "R2";
        public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
        public int Priority => 0;
        public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input) =>
            new("파일을 이동할까요?", "파일 1개를 Archive 폴더로 이동합니다.", "덮어쓰지 않습니다.");
        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["succeeded"] = 1,
                    ["failed"] = 0,
                    ["items"] = Array.Empty<object>(),
                },
                ActivitySummary: "파일 1개를 이동했어요. 실패 0개."));
        }
    }

    private sealed class FakeAuditSink : IToolAuditSink
    {
        public ConcurrentQueue<ToolApprovalAuditRecord> Approvals { get; } = new();
        public ConcurrentQueue<ToolExecutionAuditRecord> Executions { get; } = new();
        public Task RecordApprovalAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken)
        {
            Approvals.Enqueue(record);
            return Task.CompletedTask;
        }
        public Task RecordExecutionAsync(ToolExecutionAuditRecord record, CancellationToken cancellationToken)
        {
            Executions.Enqueue(record);
            return Task.CompletedTask;
        }
    }
}
