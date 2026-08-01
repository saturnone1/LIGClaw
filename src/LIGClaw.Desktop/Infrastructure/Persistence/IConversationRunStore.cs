using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal interface IConversationRunStore
{
    Task StartRunAsync(
        string conversationId,
        string runId,
        string userInput,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationContextMessage>> GetConversationContextAsync(
        string conversationId,
        int maximumMessages = 40,
        int maximumCharacters = 64_000,
        CancellationToken cancellationToken = default);

    Task AppendEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default);

    Task MarkRunAsync(
        string conversationId,
        string runId,
        string status,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);
}
