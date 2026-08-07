using System.IO;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Sidecar;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ConversationStartOutcome(
    bool RefreshConversations,
    string? Diagnostic);

internal sealed record ConversationCancelOutcome(
    bool Requested,
    string Status,
    string? Diagnostic = null);

internal sealed class ConversationOrchestrationController(
    ConversationRunController conversationRun,
    IConversationSidecar sidecar,
    IConversationRunStore store,
    Func<IReadOnlyDictionary<string, object?>?> loadProviderRouting,
    Func<DateTimeOffset>? getUtcNow = null)
{
    private readonly Func<DateTimeOffset> _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);

    public ConversationRunIdentity BeginRun() => conversationRun.BeginRun();

    public async Task<ConversationStartOutcome> StartAsync(
        ConversationRunIdentity identity,
        string input,
        bool persistenceAvailable)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? history = null;
        string? diagnostic = null;
        var refreshConversations = false;
        if (persistenceAvailable)
        {
            try
            {
                await store.StartRunAsync(
                    identity.ConversationId,
                    identity.RunId,
                    input,
                    _getUtcNow());
                var context = await store.GetConversationContextAsync(
                    identity.ConversationId);
                history = context
                    .Select(message => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["role"] = message.Role,
                        ["content"] = message.Content,
                    })
                    .ToArray();
                refreshConversations = true;
            }
            catch (Exception exception)
            {
                diagnostic = $"대화 시작 기록 실패: {exception.GetType().Name}";
            }
        }

        var started = await sidecar.StartConversationAsync(
            identity.ConversationId,
            identity.RunId,
            input,
            "cline",
            history,
            identity.CancellationToken,
            loadProviderRouting());
        if (!started.Accepted || !StringComparer.Ordinal.Equals(started.RunId, identity.RunId))
            throw new InvalidDataException("Sidecar가 요청 실행 ID를 확인하지 못했습니다.");
        return new ConversationStartOutcome(refreshConversations, diagnostic);
    }

    public bool TryRequestCancellation(out ActiveConversationRun? activeRun) =>
        conversationRun.TryRequestCancellation(out activeRun);

    public async Task<ConversationCancelOutcome> CancelAsync(ActiveConversationRun activeRun)
    {
        ArgumentNullException.ThrowIfNull(activeRun);
        try
        {
            var result = await sidecar.CancelConversationAsync(activeRun.ConversationId);
            return new ConversationCancelOutcome(
                true,
                result.Cancelled ? "요청을 중단하고 있어요…" : "이미 처리가 끝났어요.");
        }
        catch (Exception exception)
        {
            return new ConversationCancelOutcome(
                true,
                "중단 요청을 전달하지 못했어요. 현재 요청 상태를 확인하고 있어요…",
                $"요청 중단 실패: {exception.GetType().Name}");
        }
    }

    public async Task<string?> PersistEventAsync(AgentEvent agentEvent, bool persistenceAvailable)
    {
        if (!persistenceAvailable) return null;
        try
        {
            await store.AppendEventAsync(agentEvent);
            return null;
        }
        catch (Exception exception)
        {
            return $"대화 이벤트 기록 실패: {exception.GetType().Name}";
        }
    }

    public async Task<string?> MarkRunAsync(
        string conversationId,
        string runId,
        string status,
        bool persistenceAvailable)
    {
        if (!persistenceAvailable) return null;
        try
        {
            await store.MarkRunAsync(conversationId, runId, status, _getUtcNow());
            return null;
        }
        catch (Exception exception)
        {
            return $"대화 상태 기록 실패: {exception.GetType().Name}";
        }
    }
}
