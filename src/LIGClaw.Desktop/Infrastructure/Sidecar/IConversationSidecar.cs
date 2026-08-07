using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Sidecar;

public interface IConversationSidecar
{
    Task<ConversationStartResult> StartConversationAsync(
        string conversationId,
        string runId,
        string input,
        string runtime,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? history = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object?>? providerRouting = null);

    Task<ConversationCancelResult> CancelConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
}
