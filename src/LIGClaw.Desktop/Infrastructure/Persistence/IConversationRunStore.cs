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

internal interface IConversationRepository : IConversationRunStore
{
    Task MarkRunningConversationsInterruptedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> GetRecentConversationsAsync(int limit = 12, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> SearchConversationsAsync(string query, int limit = 50, CancellationToken cancellationToken = default);
    Task<string> GetTranscriptAsync(string conversationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationTurn>> GetConversationTurnsAsync(string conversationId, CancellationToken cancellationToken = default);
}
