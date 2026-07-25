using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;

namespace LIGClaw.Sidecar.IntegrationTests;

public sealed class SidecarConversationTests
{
    [Fact(Timeout = 30_000)]
    public async Task StreamsReplayAndClineRunsAndCancelsActiveRun()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sidecarPath = Path.Combine(repositoryRoot, "sidecar", "dist", "index.js");
        Assert.True(File.Exists(sidecarPath), "Build the Sidecar before running integration tests.");

        var pipeName = $"ligclaw-test-{Environment.ProcessId}-{Guid.NewGuid():N}";
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
            var events = new ConcurrentQueue<AgentEvent>();
            var terminalEvents = new ConcurrentDictionary<string, TaskCompletionSource<AgentEvent>>();
            client.NotificationReceived += (_, notification) =>
            {
                if (notification.Method != "agent.event" || notification.Params is null) return;
                var agentEvent = notification.Params.Value.Deserialize<AgentEvent>(ContentLengthMessageStream.SerializerOptions)!;
                events.Enqueue(agentEvent);
                if (agentEvent.Type is "run_completed" or "run_cancelled" or "run_failed")
                    terminalEvents.GetOrAdd(agentEvent.ConversationId, _ => NewCompletion()).TrySetResult(agentEvent);
            };

            var initialized = await client.InvokeAsync<InitializeResult>(
                "initialize",
                new InitializeParams(
                    ProtocolConstants.ProtocolVersion,
                    "integration-test",
                    ProtocolConstants.ContractHash,
                    sessionToken));
            Assert.Contains("agent.events", initialized.Capabilities);
            Assert.Contains("runtime.cline.0.0.65", initialized.Capabilities);

            await Assert.ThrowsAsync<RpcException>(() =>
                client.InvokeAsync<ConversationCancelResult>("conversation.cancel", null));
            _ = await client.InvokeAsync<PingResult>("ping", null);

            await RunAndAssertAsync(client, events, terminalEvents, "replay-conversation", "replay", "hello replay", "Replay runtime received: hello replay");
            await RunAndAssertAsync(
                client,
                events,
                terminalEvents,
                "cline-conversation",
                "cline",
                "hello cline",
                "요청을 확인했어요.\n\n“hello cline”\n\n현재는 대화 연결을 준비하는 단계예요. Windows 작업 기능이 연결되면 이 요청을 직접 처리할 수 있어요.");

            var cancelConversationId = "cancel-conversation";
            var cancellationTerminal = terminalEvents.GetOrAdd(cancelConversationId, _ => NewCompletion());
            var started = await client.InvokeAsync<ConversationStartResult>(
                "conversation.start",
                new ConversationStartParams(cancelConversationId, new string('x', 2_000), "replay"));
            Assert.True(started.Accepted);
            var cancelled = await client.InvokeAsync<ConversationCancelResult>(
                "conversation.cancel",
                new ConversationCancelParams(cancelConversationId));
            Assert.True(cancelled.Cancelled);
            Assert.Equal("run_cancelled", (await cancellationTerminal.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task RunAndAssertAsync(
        RpcClient client,
        ConcurrentQueue<AgentEvent> events,
        ConcurrentDictionary<string, TaskCompletionSource<AgentEvent>> terminals,
        string conversationId,
        string runtime,
        string input,
        string expectedOutput)
    {
        var terminal = terminals.GetOrAdd(conversationId, _ => NewCompletion());
        var started = await client.InvokeAsync<ConversationStartResult>(
            "conversation.start",
            new ConversationStartParams(conversationId, input, runtime));
        Assert.True(started.Accepted);
        Assert.Equal("run_completed", (await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type);

        var runEvents = events.Where(agentEvent => agentEvent.ConversationId == conversationId).ToArray();
        Assert.Equal(Enumerable.Range(0, runEvents.Length).Select(value => (long)value), runEvents.Select(agentEvent => agentEvent.Sequence));
        Assert.Equal("run_started", runEvents[0].Type);
        Assert.Equal("run_completed", runEvents[^1].Type);
        Assert.Equal(expectedOutput, string.Concat(runEvents.Where(agentEvent => agentEvent.Type == "text_delta").Select(agentEvent => agentEvent.Text)));
    }

    private static TaskCompletionSource<AgentEvent> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
}
