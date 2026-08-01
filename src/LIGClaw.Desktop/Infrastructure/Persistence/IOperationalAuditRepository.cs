using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal interface IOperationalAuditRepository : IToolAuditSink, ICapabilityGrantStore, IUndoJournal
{
    Task<IReadOnlyList<ToolActivitySummary>> GetRecentToolActivityAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UndoActivitySummary>> GetPendingUndoActivityAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);
}
