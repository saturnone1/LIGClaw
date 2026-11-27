using LIGClaw.Application.Tools;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ActiveConversationRun(
    string ConversationId,
    string RunId,
    CancellationToken CancellationToken);

internal sealed class ConversationRunController
{
    private readonly ConversationSessionState _session;
    private readonly ToolInvocationPolicy _toolPolicy;

    public ConversationRunController(
        ToolInvocationPolicy toolPolicy,
        Func<string>? createId = null)
    {
        _toolPolicy = toolPolicy;
        _session = new ConversationSessionState(createId);
    }

    public string? CurrentConversationId => _session.CurrentConversationId;
    public string? ActiveConversationId => _session.ActiveConversationId;
    public string? ActiveRunId => _session.ActiveRunId;
    public bool IsRunning => _session.IsRunActive;
    public bool IsCancelling { get; private set; }

    public ConversationRunIdentity BeginRun()
    {
        var identity = _session.BeginRun();
        try
        {
            _toolPolicy.BeginRun(identity.ConversationId, identity.RunId);
            IsCancelling = false;
            return identity;
        }
        catch
        {
            _ = _session.EndRun(identity.ConversationId, identity.RunId);
            throw;
        }
    }

    public bool TryRequestCancellation(out ActiveConversationRun? activeRun)
    {
        if (_session.ActiveConversationId is not { } conversationId ||
            _session.ActiveRunId is not { } runId ||
            !_session.TryGetCancellationToken(conversationId, runId, out var cancellationToken) ||
            !_session.CancelRun(conversationId, runId))
        {
            activeRun = null;
            return false;
        }

        IsCancelling = true;
        activeRun = new ActiveConversationRun(conversationId, runId, cancellationToken);
        return true;
    }

    public bool CompleteActiveRun()
    {
        if (_session.ActiveConversationId is not { } conversationId ||
            _session.ActiveRunId is not { } runId)
            return false;

        _toolPolicy.EndRun(conversationId, runId);
        var ended = _session.EndRun(conversationId, runId);
        if (ended) IsCancelling = false;
        return ended;
    }

    public bool IsActiveRun(string conversationId, string runId) =>
        _toolPolicy.IsActiveRun(conversationId, runId);

    public bool TryGetCancellationToken(
        string conversationId,
        string runId,
        out CancellationToken cancellationToken) =>
        _session.TryGetCancellationToken(conversationId, runId, out cancellationToken);

    public bool SelectConversation(string conversationId) =>
        _session.SelectConversation(conversationId);

    public bool StartNewConversation() => _session.StartNewConversation();
}
