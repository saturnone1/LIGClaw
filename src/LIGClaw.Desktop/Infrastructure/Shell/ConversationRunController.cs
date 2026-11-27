using LIGClaw.Application.Capabilities;
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
    private readonly CapabilityRunAuthorizationPolicy _capabilityPolicy;
    private readonly object _cancellationHistoryGate = new();
    private CancelledRunIdentity? _lastExplicitlyCancelledRun;
    private long _runSequence;

    public ConversationRunController(
        ToolInvocationPolicy toolPolicy,
        Func<string>? createId = null,
        CapabilityRunAuthorizationPolicy? capabilityPolicy = null)
    {
        _toolPolicy = toolPolicy;
        _capabilityPolicy = capabilityPolicy ?? new CapabilityRunAuthorizationPolicy();
        _session = new ConversationSessionState(createId);
    }

    public string? CurrentConversationId => _session.CurrentConversationId;
    public string? ActiveConversationId => _session.ActiveConversationId;
    public string? ActiveRunId => _session.ActiveRunId;
    public bool IsRunning => _session.IsRunActive;
    public bool IsCancelling { get; private set; }
    public long RunSequence => Interlocked.Read(ref _runSequence);

    public ConversationRunIdentity BeginRun()
    {
        var identity = _session.BeginRun();
        try
        {
            _toolPolicy.BeginRun(identity.ConversationId, identity.RunId);
            try
            {
                _capabilityPolicy.BeginRun(identity.ConversationId, identity.RunId);
            }
            catch
            {
                _toolPolicy.EndRun(identity.ConversationId, identity.RunId);
                throw;
            }
            IsCancelling = false;
            Interlocked.Increment(ref _runSequence);
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
        lock (_cancellationHistoryGate)
            _lastExplicitlyCancelledRun = new CancelledRunIdentity(conversationId, runId);
        activeRun = new ActiveConversationRun(conversationId, runId, cancellationToken);
        return true;
    }

    public bool CompleteActiveRun()
    {
        if (_session.ActiveConversationId is not { } conversationId ||
            _session.ActiveRunId is not { } runId)
            return false;

        _capabilityPolicy.EndRun(conversationId, runId);
        _toolPolicy.EndRun(conversationId, runId);
        var ended = _session.EndRun(conversationId, runId);
        if (ended) IsCancelling = false;
        return ended;
    }

    public bool IsActiveRun(string conversationId, string runId) =>
        _toolPolicy.IsActiveRun(conversationId, runId);

    public bool WasCancellationRequested(string conversationId, string runId)
    {
        lock (_cancellationHistoryGate)
            return _lastExplicitlyCancelledRun is { } cancelled &&
                   StringComparer.Ordinal.Equals(cancelled.ConversationId, conversationId) &&
                   StringComparer.Ordinal.Equals(cancelled.RunId, runId);
    }

    public void BindCapabilitySnapshot(
        string conversationId,
        string runId,
        CapabilityRuntimeSnapshot snapshot) =>
        _capabilityPolicy.BindSnapshot(conversationId, runId, snapshot);

    public CapabilityRunAuthorizationDecision SelectCapability(
        CapabilityRunSelectionRequest request) =>
        _capabilityPolicy.Select(request);

    public CapabilityRunAuthorizationDecision AuthorizeCapabilityTool(
        string conversationId,
        string runId,
        string toolName,
        string risk,
        CapabilityRunInvocationContext? context) =>
        _capabilityPolicy.AuthorizeTool(conversationId, runId, toolName, risk, context);

    public CapabilityRunAuthorizationDecision AuthorizeCapabilityWorkflow(
        string conversationId,
        string runId,
        string workflowCallId,
        CapabilityRunInvocationContext? context) =>
        _capabilityPolicy.AuthorizeWorkflow(conversationId, runId, workflowCallId, context);

    public ToolAuthorizationDecision PrepareControlPlaneInvocation(
        string conversationId,
        string runId,
        string toolCallId,
        string risk) =>
        _toolPolicy.Prepare(conversationId, runId, toolCallId, risk);

    public ToolAuthorizationDecision ClaimPreparedInvocation(
        string conversationId,
        string runId,
        string toolCallId,
        string risk) =>
        _toolPolicy.ClaimPrepared(conversationId, runId, toolCallId, risk);

    public bool TryGetCancellationToken(
        string conversationId,
        string runId,
        out CancellationToken cancellationToken) =>
        _session.TryGetCancellationToken(conversationId, runId, out cancellationToken);

    public bool SelectConversation(string conversationId) =>
        _session.SelectConversation(conversationId);

    public bool StartNewConversation() => _session.StartNewConversation();

    private sealed record CancelledRunIdentity(string ConversationId, string RunId);
}
