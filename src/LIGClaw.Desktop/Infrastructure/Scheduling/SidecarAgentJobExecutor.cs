using System.Collections.Concurrent;
using System.Text;
using LIGClaw.Application.Scheduling;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Infrastructure.Scheduling;

internal sealed class SidecarAgentJobExecutor(
    SidecarSupervisor sidecar,
    WindowsToolHost toolHost,
    Func<WindowsToolApprovalPrompt, bool, CancellationToken, Task<ToolApprovalChoice>> requestApproval,
    Func<string?, ModelRoutingSettings?> loadRouting,
    IToolAuditSink? auditSink = null,
    ICapabilityGrantStore? grantStore = null) : IAgentJobExecutor
{
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _effects = new(1, 1);

    public bool OwnsRun(string runId) => _runs.ContainsKey(runId);

    public async Task<AgentJobRunResult> ExecuteAsync(
        AgentJobClaim claim,
        CancellationToken cancellationToken)
    {
        var conversationId = $"agent-job:{claim.Job.Id}";
        var policy = new ToolInvocationPolicy();
        policy.BeginRun(conversationId, claim.RunId);
        var coordinator = new ToolInvocationCoordinator(
            policy, toolHost, requestApproval, auditSink, grantStore);
        var run = new ActiveRun(
            conversationId,
            claim.RunId,
            claim.Job.ResultMaxCharacters,
            policy,
            coordinator,
            cancellationToken);
        if (!_runs.TryAdd(claim.RunId, run))
            return new AgentJobRunResult(false, ErrorCode: "duplicate_run_id");

        try
        {
            var routing = loadRouting(claim.Job.ModelProfileId);
            if (routing is null)
                return new AgentJobRunResult(false, ErrorCode: "model_profile_unavailable");
            var started = await sidecar.StartConversationAsync(
                conversationId,
                claim.RunId,
                claim.Job.Prompt,
                "cline",
                history: null,
                cancellationToken,
                ModelRoutingPayload.Create(routing)).ConfigureAwait(false);
            if (!started.Accepted || !StringComparer.Ordinal.Equals(started.RunId, claim.RunId))
                return new AgentJobRunResult(false, ErrorCode: "sidecar_rejected");

            using var registration = cancellationToken.Register(() =>
                run.Completion.TrySetCanceled(cancellationToken));
            try
            {
                return await run.Completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _ = await sidecar.CancelConversationAsync(conversationId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Scheduler timeout is the durable source of truth; Sidecar restart recovery is separate.
                }
                throw;
            }
        }
        finally
        {
            _runs.TryRemove(claim.RunId, out _);
            policy.EndRun(conversationId, claim.RunId);
        }
    }

    public bool TryHandleAgentEvent(AgentEvent agentEvent)
    {
        if (!_runs.TryGetValue(agentEvent.RunId, out var run) ||
            !StringComparer.Ordinal.Equals(run.ConversationId, agentEvent.ConversationId)) return false;

        switch (agentEvent.Type)
        {
            case "routing_changed":
                run.AppendRouting(agentEvent.Message);
                break;
            case "text_delta":
                run.Append(agentEvent.Text);
                break;
            case "run_completed":
                run.Completion.TrySetResult(new AgentJobRunResult(true, run.ResultText));
                break;
            case "run_cancelled":
                run.Completion.TrySetResult(new AgentJobRunResult(false, run.ResultText, "runtime_cancelled"));
                break;
            case "run_failed":
                run.Completion.TrySetResult(new AgentJobRunResult(false, run.ResultText, "runtime_failed"));
                break;
        }
        return true;
    }

    public async Task<bool> TryHandleToolInvocationAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(invocation.RunId, out var run) ||
            !StringComparer.Ordinal.Equals(run.ConversationId, invocation.ConversationId)) return false;

        WindowsToolExecutionResult execution;
        try
        {
            await _effects.WaitAsync(run.CancellationToken).ConfigureAwait(false);
            try { execution = await run.Coordinator.ExecuteAsync(invocation, run.CancellationToken).ConfigureAwait(false); }
            finally { _effects.Release(); }
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            execution = new WindowsToolExecutionResult(
                false, new Dictionary<string, object?>(), "백그라운드 작업이 중단됐어요.");
        }
        catch
        {
            execution = new WindowsToolExecutionResult(
                false, new Dictionary<string, object?>(), "백그라운드 Windows 기능을 안전하게 처리하지 못했어요.");
        }

        try
        {
            _ = await sidecar.SubmitToolResultAsync(
                new ToolResultParams(invocation.ToolCallId, execution.Success, execution.Output, execution.Error),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            run.Completion.TrySetResult(new AgentJobRunResult(false, run.ResultText, "tool_result_delivery_failed"));
        }
        return true;
    }

    private sealed class ActiveRun(
        string conversationId,
        string runId,
        int maximumCharacters,
        ToolInvocationPolicy policy,
        ToolInvocationCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        private readonly object _sync = new();
        private readonly StringBuilder _result = new();

        public string ConversationId { get; } = conversationId;
        public string RunId { get; } = runId;
        public int MaximumCharacters { get; } = maximumCharacters;
        public ToolInvocationPolicy Policy { get; } = policy;
        public ToolInvocationCoordinator Coordinator { get; } = coordinator;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<AgentJobRunResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ResultText
        {
            get { lock (_sync) return _result.ToString(); }
        }

        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_sync)
            {
                var remaining = MaximumCharacters - _result.Length;
                if (remaining <= 0) return;
                _result.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
            }
        }

        public void AppendRouting(string? message)
        {
            var parts = message?.Split('|', 2);
            if (parts is not { Length: 2 }) return;
            Append($"[모델 전환: {parts[0]} · {parts[1]}]\n");
        }
    }
}
