using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;
using LIGClaw.Desktop.Infrastructure.Sidecar;

namespace LIGClaw.Desktop.Tests;

public sealed class SidecarSupervisorProtocolTests
{
    [Fact]
    public async Task DispatchesOnlyBoundedWorkflowInvokeNotifications()
    {
        await using var supervisor = new SidecarSupervisor();
        WorkflowInvokeParams? received = null;
        supervisor.WorkflowInvocationReceived += (_, invocation) => received = invocation;
        var invocation = new WorkflowInvokeParams(
            $"wfinv_{new string('a', 48)}",
            "conversation",
            "parent-run",
            "workflow.invoke.v1",
            new CapabilityInvocationContext(
                $"sha256:{new string('b', 64)}",
                "cap_0123456789abcdef01234567",
                "company.sample.workflow",
                "1.0.0",
                $"sha256:{new string('c', 64)}",
                1,
                $"sha256:{new string('e', 64)}"),
            $"wfid_{new string('d', 64)}",
            DateTimeOffset.UtcNow.AddMinutes(8),
            new Dictionary<string, object?> { ["value"] = "bounded" });

        supervisor.HandleNotification(
            null,
            new RpcNotification(
                "workflow.invoke",
                JsonSerializer.SerializeToElement(
                    invocation,
                    ContentLengthMessageStream.SerializerOptions)));

        Assert.Equal(invocation.WorkflowCallId, received!.WorkflowCallId);
        Assert.Equal(invocation.IdempotencyKey, received.IdempotencyKey);
        Assert.Equal(invocation.CapabilityContext, received.CapabilityContext);
    }

    [Fact(Timeout = 10_000)]
    public async Task RoutingChangedReplayCrossesNamedPipeWithoutBreakingEventSequence()
    {
        var replay = LoadReplay("routing-changed-agent-events.json");
        var pipeName = $"ligclaw-routing-replay-{Environment.ProcessId}-{Guid.NewGuid():N}";
        await using var desktopPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var sidecarPipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var connect = sidecarPipe.ConnectAsync();
        await desktopPipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await connect.WaitAsync(TimeSpan.FromSeconds(2));

        await using var rpcClient = new RpcClient(desktopPipe);
        await using var supervisor = new SidecarSupervisor();
        var received = new ConcurrentQueue<AgentEvent>();
        var diagnostics = new ConcurrentQueue<string>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.AgentEventReceived += (_, agentEvent) =>
        {
            received.Enqueue(agentEvent);
            if (agentEvent.Type == "run_completed") terminal.TrySetResult();
        };
        supervisor.DiagnosticMessage += (_, message) => diagnostics.Enqueue(message);
        rpcClient.NotificationReceived += supervisor.HandleNotification;

        var replayTask = ReplayAsync(sidecarPipe, replay);
        _ = await rpcClient.InvokeAsync<PingResult>("ping", null);
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await replayTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            ["routing_changed", "run_started", "text_delta", "run_completed"],
            received.Select(agentEvent => agentEvent.Type));
        Assert.Equal("backup|provider_unavailable", received.First().Message);
        Assert.DoesNotContain(diagnostics, message =>
            message.Contains("invalid Sidecar notification", StringComparison.Ordinal) ||
            message.Contains("out-of-order agent event", StringComparison.Ordinal));
    }

    private static async Task ReplayAsync(NamedPipeClientStream pipe, IReadOnlyList<AgentEvent> replay)
    {
        var messages = new ContentLengthMessageStream(pipe);
        using var requestDocument = await messages.ReadAsync();
        var request = requestDocument.RootElement.Deserialize<RpcRequest>(ContentLengthMessageStream.SerializerOptions)
            ?? throw new InvalidDataException("Replay did not receive an RPC request.");

        foreach (var agentEvent in replay)
        {
            await messages.WriteAsync(new RpcNotification(
                "agent.event",
                JsonSerializer.SerializeToElement(agentEvent, ContentLengthMessageStream.SerializerOptions)));
        }

        await messages.WriteAsync(new RpcResponse(
            request.Id,
            JsonSerializer.SerializeToElement(
                new PingResult(DateTimeOffset.UnixEpoch),
                ContentLengthMessageStream.SerializerOptions),
            null));
    }

    private static IReadOnlyList<AgentEvent> LoadReplay(string fileName)
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "replays", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        return document.RootElement.GetProperty("events")
            .EnumerateArray()
            .Select(item => item.Deserialize<AgentEvent>(ContentLengthMessageStream.SerializerOptions)
                ?? throw new InvalidDataException($"Replay '{fileName}' contains an empty event."))
            .ToArray();
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
