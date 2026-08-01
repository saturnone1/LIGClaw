using System.Globalization;
using System.IO;
using LIGClaw.Application.Agents;
using LIGClaw.Application.Memory;
using LIGClaw.Application.Scheduling;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed record ConversationTurn(
    string RunId,
    string UserInput,
    string AssistantText,
    string Status,
    DateTimeOffset CreatedAtUtc);

internal sealed partial class ConversationStore : IConversationRepository, IToolAuditSink, ICapabilityGrantStore, IUndoJournal, IPersonalMemoryRepository, IScheduleRepository, IAgentJobRepository, ILocalSubagentRepository, IDisposable
{
    private readonly ConversationDatabase _database;
    private readonly ConversationRepository _conversations;
    private readonly OperationalAuditRepository _operations;
    private readonly PersonalMemoryRepository _personalMemories;
    private readonly ScheduleRepository _schedules;

    public ConversationStore(string databasePath)
    {
        _database = new ConversationDatabase(databasePath);
        _conversations = new ConversationRepository(_database);
        _operations = new OperationalAuditRepository(_database);
        _personalMemories = new PersonalMemoryRepository(_database);
        _schedules = new ScheduleRepository(_database);
    }

    public string DatabasePath => _database.DatabasePath;
    public IConversationRepository Conversations => _conversations;
    public IOperationalAuditRepository Operations => _operations;
    public IPersonalMemoryRepository Memories => _personalMemories;
    public IScheduleRepository Schedules => _schedules;

    public static ConversationStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LIGClaw",
            "Data");
        return new ConversationStore(Path.Combine(directory, "ligclaw.db"));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _database.InitializeAsync(cancellationToken);

    public Task StartRunAsync(string conversationId, string runId, string userInput, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) =>
        _conversations.StartRunAsync(conversationId, runId, userInput, startedAtUtc, cancellationToken);

    public Task AppendEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default) =>
        _conversations.AppendEventAsync(agentEvent, cancellationToken);

    public Task MarkRunAsync(string conversationId, string runId, string status, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
        _conversations.MarkRunAsync(conversationId, runId, status, updatedAtUtc, cancellationToken);

    public Task MarkRunningConversationsInterruptedAsync(CancellationToken cancellationToken = default) =>
        _conversations.MarkRunningConversationsInterruptedAsync(cancellationToken);

    public Task<MemoryUpsertResult> UpsertAsync(PersonalMemoryDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _personalMemories.UpsertAsync(draft, nowUtc, cancellationToken);

    public Task<IReadOnlyList<PersonalMemory>> ListAsync(string? query, int limit, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _personalMemories.ListAsync(query, limit, nowUtc, cancellationToken);

    public Task<PersonalMemory?> GetAsync(string memoryId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _personalMemories.GetAsync(memoryId, nowUtc, cancellationToken);

    public Task<IReadOnlyList<PersonalMemory>> ListForManagementAsync(string? query, int offset, int limit, CancellationToken cancellationToken) =>
        _personalMemories.ListForManagementAsync(query, offset, limit, cancellationToken);

    public Task<PersonalMemory?> GetForManagementAsync(string memoryId, CancellationToken cancellationToken) =>
        _personalMemories.GetForManagementAsync(memoryId, cancellationToken);

    public Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken) =>
        _personalMemories.DeleteAsync(memoryId, cancellationToken);

    public Task<IReadOnlyList<SemanticMemoryCandidate>> GetSemanticMemoryCandidatesAsync(string modelKey, DateTimeOffset nowUtc, int limit = 500, CancellationToken cancellationToken = default) =>
        _personalMemories.GetSemanticMemoryCandidatesAsync(modelKey, nowUtc, limit, cancellationToken);

    public Task UpsertMemoryEmbeddingAsync(string memoryId, string modelKey, string contentHash, IReadOnlyList<float> vector, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken) =>
        _personalMemories.UpsertMemoryEmbeddingAsync(memoryId, modelKey, contentHash, vector, updatedAtUtc, cancellationToken);

    public Task<string?> ResolveValueAsync(string kind, string key, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _personalMemories.ResolveValueAsync(kind, key, nowUtc, cancellationToken);

    public Task<ScheduledNotification> CreateAsync(NotificationScheduleDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _schedules.CreateAsync(draft, nowUtc, cancellationToken);

    public Task<ScheduledNotification?> UpdateAsync(string jobId, NotificationScheduleDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _schedules.UpdateAsync(jobId, draft, nowUtc, cancellationToken);

    public Task<IReadOnlyList<ScheduledNotification>> ListAsync(bool includeInactive, int offset, int limit, CancellationToken cancellationToken) =>
        _schedules.ListAsync(includeInactive, offset, limit, cancellationToken);

    public Task<ScheduledNotification?> GetAsync(string jobId, CancellationToken cancellationToken) =>
        _schedules.GetAsync(jobId, cancellationToken);

    public Task<bool> CancelAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _schedules.CancelAsync(jobId, nowUtc, cancellationToken);

    public Task<ScheduleRecoveryResult> ReconcileOnStartupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _schedules.ReconcileOnStartupAsync(nowUtc, cancellationToken);

    public Task<ScheduleRecoveryResult> ReconcileOverdueAsync(DateTimeOffset nowUtc, DateTimeOffset dueBeforeUtc, CancellationToken cancellationToken) =>
        _schedules.ReconcileOverdueAsync(nowUtc, dueBeforeUtc, cancellationToken);

    public Task<ScheduledJobClaim?> TryClaimDueAsync(DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken) =>
        _schedules.TryClaimDueAsync(nowUtc, leaseUntilUtc, cancellationToken);

    public Task CompleteRunAsync(string runId, DateTimeOffset completedAtUtc, bool succeeded, string? errorCode, CancellationToken cancellationToken) =>
        _schedules.CompleteRunAsync(runId, completedAtUtc, succeeded, errorCode, cancellationToken);

    public Task<bool> ResolveMisfireAsync(string jobId, bool runNow, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _schedules.ResolveMisfireAsync(jobId, runNow, nowUtc, cancellationToken);
    public Task<IReadOnlyList<ConversationSummary>> GetRecentConversationsAsync(int limit = 12, CancellationToken cancellationToken = default) =>
        _conversations.GetRecentConversationsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<ConversationSummary>> SearchConversationsAsync(string query, int limit = 50, CancellationToken cancellationToken = default) =>
        _conversations.SearchConversationsAsync(query, limit, cancellationToken);

    public Task<string> GetTranscriptAsync(string conversationId, CancellationToken cancellationToken = default) =>
        _conversations.GetTranscriptAsync(conversationId, cancellationToken);

    public Task<IReadOnlyList<ConversationTurn>> GetConversationTurnsAsync(string conversationId, CancellationToken cancellationToken = default) =>
        _conversations.GetConversationTurnsAsync(conversationId, cancellationToken);

    public Task<IReadOnlyList<ConversationContextMessage>> GetConversationContextAsync(string conversationId, int maximumMessages = 40, int maximumCharacters = 64_000, CancellationToken cancellationToken = default) =>
        _conversations.GetConversationContextAsync(conversationId, maximumMessages, maximumCharacters, cancellationToken);

    public Task RecordApprovalAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken) =>
        _operations.RecordApprovalAsync(record, cancellationToken);

    public Task RecordExecutionAsync(ToolExecutionAuditRecord record, CancellationToken cancellationToken) =>
        _operations.RecordExecutionAsync(record, cancellationToken);

    public Task<CapabilityGrant?> FindActiveGrantAsync(string toolName, string risk, string scopeHash, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _operations.FindActiveGrantAsync(toolName, risk, scopeHash, nowUtc, cancellationToken);

    public Task SaveGrantAsync(CapabilityGrant grant, CancellationToken cancellationToken) =>
        _operations.SaveGrantAsync(grant, cancellationToken);

    public Task<IReadOnlyList<CapabilityGrant>> GetActiveGrantsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        _operations.GetActiveGrantsAsync(nowUtc, cancellationToken);

    public Task RevokeGrantAsync(string grantId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken = default) =>
        _operations.RevokeGrantAsync(grantId, revokedAtUtc, cancellationToken);

    public Task<IReadOnlyList<ToolActivitySummary>> GetRecentToolActivityAsync(int limit = 50, CancellationToken cancellationToken = default) =>
        _operations.GetRecentToolActivityAsync(limit, cancellationToken);

    public Task<string> CreateUndoAsync(string kind, string originalPath, string currentPath, string currentIdentity, CancellationToken cancellationToken) =>
        _operations.CreateUndoAsync(kind, originalPath, currentPath, currentIdentity, cancellationToken);

    public Task<FileUndoEntry?> GetPendingUndoAsync(string undoId, CancellationToken cancellationToken) =>
        _operations.GetPendingUndoAsync(undoId, cancellationToken);

    public Task MarkUndoneAsync(string undoId, DateTimeOffset undoneAtUtc, CancellationToken cancellationToken) =>
        _operations.MarkUndoneAsync(undoId, undoneAtUtc, cancellationToken);

    public Task<IReadOnlyList<UndoActivitySummary>> GetPendingUndoActivityAsync(int limit = 20, CancellationToken cancellationToken = default) =>
        _operations.GetPendingUndoActivityAsync(limit, cancellationToken);

    private static async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private Task WithConnectionAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken) =>
        _database.WithConnectionAsync(operation, cancellationToken);

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _database.Dispose();
}
