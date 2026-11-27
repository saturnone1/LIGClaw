using System.Collections.Concurrent;
using System.Text;
using LIGClaw.Application.Agents;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Scheduling;

internal sealed class LocalSubagentOrchestrator(
    SidecarSupervisor sidecar,
    WindowsToolHost toolHost,
    ILocalSubagentRepository repository,
    Func<WindowsToolApprovalPrompt, bool, CancellationToken, Task<ToolApprovalChoice>> requestApproval,
    Func<string?, ModelRoutingSettings?> loadRouting,
    IToolAuditSink? auditSink = null,
    ICapabilityGrantStore? grantStore = null) : ILocalSubagentOrchestrator
{
    private const int MaximumConcurrency = 3;
    private readonly ConcurrentDictionary<string, ChildRun> _runs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _effects = new(1, 1);

    public bool OwnsRun(string runId) => _runs.ContainsKey(runId);

    public async Task<SubagentBatchResult> ExecuteAsync(
        SubagentBatchDraft draft,
        CancellationToken cancellationToken)
    {
        if (!SubagentPolicy.IsValid(draft)) throw new ArgumentException("하위 Agent 작업이 올바르지 않습니다.");
        var routing = loadRouting(draft.ModelProfileId) ??
            throw new InvalidOperationException("사용할 모델 프로필을 찾을 수 없습니다.");
        var batchId = Guid.NewGuid().ToString("N");
        var records = await repository.CreateSubagentBatchAsync(
            batchId, draft, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        using var concurrency = new SemaphoreSlim(MaximumConcurrency, MaximumConcurrency);
        var tasks = records.Select(async record =>
        {
            var entered = false;
            try
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                return await ExecuteChildAsync(record, draft, routing, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await CompleteAsync(
                    record, false, null, "parent_cancelled", CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (entered) concurrency.Release();
            }
        }).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new SubagentBatchResult(batchId, results.OrderBy(result =>
            Array.FindIndex(records.ToArray(), record => record.ChildRunId == result.ChildRunId)).ToArray());
    }

    private async Task<SubagentTaskResult> ExecuteChildAsync(
        SubagentTaskRecord record,
        SubagentBatchDraft draft,
        ModelRoutingSettings routing,
        CancellationToken cancellationToken)
    {
        var conversationId = $"subagent:{record.BatchId}:{record.Ordinal}";
        var policy = new ToolInvocationPolicy();
        policy.BeginRun(conversationId, record.ChildRunId);
        var coordinator = new ToolInvocationCoordinator(policy, toolHost, requestApproval, auditSink, grantStore);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(draft.MaxRuntimeSeconds));
        var run = new ChildRun(
            conversationId, record, draft.MaxRisk, draft.ResultMaxCharacters, policy, coordinator, timeout.Token);
        if (!_runs.TryAdd(record.ChildRunId, run))
            return await CompleteAsync(record, false, null, "duplicate_run_id", CancellationToken.None).ConfigureAwait(false);
        try
        {
            var started = await sidecar.StartConversationAsync(
                conversationId, record.ChildRunId, record.Prompt, "cline", null, timeout.Token,
                ModelRoutingPayload.Create(routing)).ConfigureAwait(false);
            if (!started.Accepted)
                return await CompleteAsync(record, false, null, "sidecar_rejected", CancellationToken.None).ConfigureAwait(false);
            using var registration = timeout.Token.Register(() => run.Completion.TrySetCanceled(timeout.Token));
            try
            {
                var result = await run.Completion.Task.ConfigureAwait(false);
                return await CompleteAsync(
                    record, result.Succeeded, result.ResultText, result.ErrorCode, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { _ = await sidecar.CancelConversationAsync(conversationId, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                var code = cancellationToken.IsCancellationRequested ? "parent_cancelled" : "runtime_timeout";
                return await CompleteAsync(record, false, run.ResultText, code, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            return await CompleteAsync(record, false, run.ResultText, "runtime_error", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _runs.TryRemove(record.ChildRunId, out _);
            policy.EndRun(conversationId, record.ChildRunId);
        }
    }

    private async Task<SubagentTaskResult> CompleteAsync(
        SubagentTaskRecord record,
        bool succeeded,
        string? result,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await repository.CompleteSubagentTaskAsync(
            record.ChildRunId, succeeded, result, errorCode, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return new SubagentTaskResult(record.ChildRunId, record.Title, succeeded, result, errorCode);
    }

    public bool TryHandleAgentEvent(AgentEvent agentEvent)
    {
        if (!_runs.TryGetValue(agentEvent.RunId, out var run) ||
            !StringComparer.Ordinal.Equals(run.ConversationId, agentEvent.ConversationId)) return false;
        switch (agentEvent.Type)
        {
            case "routing_changed": run.AppendRouting(agentEvent.Message); break;
            case "text_delta": run.Append(agentEvent.Text); break;
            case "run_completed":
                run.Completion.TrySetResult(new SubagentTaskResult(
                run.Record.ChildRunId, run.Record.Title, true, run.ResultText, null)); break;
            case "run_cancelled":
                run.Completion.TrySetResult(new SubagentTaskResult(
                run.Record.ChildRunId, run.Record.Title, false, run.ResultText, "runtime_cancelled")); break;
            case "run_failed":
                run.Completion.TrySetResult(new SubagentTaskResult(
                run.Record.ChildRunId, run.Record.Title, false, run.ResultText, "runtime_failed")); break;
        }
        return true;
    }

    public async Task<bool> TryHandleToolInvocationAsync(ToolInvokeParams invocation)
    {
        if (!_runs.TryGetValue(invocation.RunId, out var run) ||
            !StringComparer.Ordinal.Equals(run.ConversationId, invocation.ConversationId)) return false;
        WindowsToolExecutionResult execution;
        if (invocation.Name == "subagent.run.v1")
        {
            execution = Failure("하위 Agent는 추가 하위 Agent를 만들 수 없습니다.");
        }
        else if (RiskRank(invocation.Risk) > RiskRank(run.MaxRisk))
        {
            execution = Failure($"하위 Agent 권한 상한 {run.MaxRisk}보다 높은 동작을 거부했어요.");
        }
        else
        {
            try
            {
                await _effects.WaitAsync(run.CancellationToken).ConfigureAwait(false);
                try { execution = await run.Coordinator.ExecuteAsync(invocation, run.CancellationToken).ConfigureAwait(false); }
                finally { _effects.Release(); }
            }
            catch (OperationCanceledException)
            {
                execution = Failure("부모 요청 또는 하위 Agent가 중단되어 Windows 작업을 실행하지 않았어요.");
            }
            catch { execution = Failure("하위 Agent Windows 작업을 안전하게 처리하지 못했어요."); }
        }
        try
        {
            _ = await sidecar.SubmitToolResultAsync(new ToolResultParams(
                invocation.ToolCallId, execution.Success, execution.Output, execution.Error)).ConfigureAwait(false);
        }
        catch
        {
            run.Completion.TrySetResult(new SubagentTaskResult(
                run.Record.ChildRunId, run.Record.Title, false, run.ResultText, "tool_result_delivery_failed"));
        }
        return true;
    }

    private static int RiskRank(string risk) => risk switch
    {
        "R0" => 0,
        "R1" => 1,
        "R2" => 2,
        "R3" => 3,
        "R4" => 4,
        _ => int.MaxValue,
    };

    private static WindowsToolExecutionResult Failure(string error) =>
        new(false, new Dictionary<string, object?>(), error);

    private sealed class ChildRun(
        string conversationId,
        SubagentTaskRecord record,
        string maxRisk,
        int maximumCharacters,
        ToolInvocationPolicy policy,
        ToolInvocationCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        private readonly object _sync = new();
        private readonly StringBuilder _result = new();
        public string ConversationId { get; } = conversationId;
        public SubagentTaskRecord Record { get; } = record;
        public string MaxRisk { get; } = maxRisk;
        public ToolInvocationPolicy Policy { get; } = policy;
        public ToolInvocationCoordinator Coordinator { get; } = coordinator;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<SubagentTaskResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResultText { get { lock (_sync) return _result.ToString(); } }
        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_sync)
            {
                var remaining = maximumCharacters - _result.Length;
                if (remaining > 0) _result.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
            }
        }
        public void AppendRouting(string? message)
        {
            var parts = message?.Split('|', 2);
            if (parts is { Length: 2 }) Append($"[모델 전환: {parts[0]} · {parts[1]}]\n");
        }
    }
}
