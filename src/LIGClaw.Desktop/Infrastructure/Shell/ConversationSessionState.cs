namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ConversationRunIdentity(
    string ConversationId,
    string RunId,
    bool IsNewConversation,
    CancellationToken CancellationToken);

internal sealed class ConversationSessionState(Func<string>? createId = null)
{
    private readonly Func<string> _createId = createId ?? (() => Guid.NewGuid().ToString("N"));
    private readonly object _sync = new();
    private CancellationTokenSource? _activeRunCancellation;

    public string? CurrentConversationId { get; private set; }
    public string? ActiveConversationId { get; private set; }
    public string? ActiveRunId { get; private set; }
    public bool IsRunActive => ActiveConversationId is not null;

    public ConversationRunIdentity BeginRun()
    {
        lock (_sync)
        {
            if (IsRunActive) throw new InvalidOperationException("A conversation run is already active.");
            var isNew = CurrentConversationId is null;
            CurrentConversationId ??= CreateRequiredId();
            ActiveConversationId = CurrentConversationId;
            ActiveRunId = CreateRequiredId();
            _activeRunCancellation = new CancellationTokenSource();
            return new ConversationRunIdentity(
                ActiveConversationId,
                ActiveRunId,
                isNew,
                _activeRunCancellation.Token);
        }
    }

    public bool EndRun(string conversationId, string runId)
    {
        lock (_sync)
        {
            if (!MatchesActiveRun(conversationId, runId)) return false;
            _activeRunCancellation?.Cancel();
            _activeRunCancellation?.Dispose();
            _activeRunCancellation = null;
            ActiveConversationId = null;
            ActiveRunId = null;
            return true;
        }
    }

    public bool CancelRun(string conversationId, string runId)
    {
        lock (_sync)
        {
            if (!MatchesActiveRun(conversationId, runId)) return false;
            _activeRunCancellation?.Cancel();
            return true;
        }
    }

    public bool TryGetCancellationToken(
        string conversationId,
        string runId,
        out CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (MatchesActiveRun(conversationId, runId) && _activeRunCancellation is not null)
            {
                cancellationToken = _activeRunCancellation.Token;
                return true;
            }
            cancellationToken = CancellationToken.None;
            return false;
        }
    }

    public bool SelectConversation(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (IsRunActive) return false;
        CurrentConversationId = conversationId;
        return true;
    }

    public bool StartNewConversation()
    {
        if (IsRunActive) return false;
        CurrentConversationId = null;
        return true;
    }

    private string CreateRequiredId()
    {
        var id = _createId();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Conversation identity generator returned an empty value.");
        return id;
    }

    private bool MatchesActiveRun(string conversationId, string runId) =>
        StringComparer.Ordinal.Equals(ActiveConversationId, conversationId) &&
        StringComparer.Ordinal.Equals(ActiveRunId, runId);
}
