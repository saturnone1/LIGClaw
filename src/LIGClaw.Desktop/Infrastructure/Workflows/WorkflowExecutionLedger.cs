using LIGClaw.Desktop.Infrastructure.Persistence;

namespace LIGClaw.Desktop.Infrastructure.Workflows;

internal interface IWorkflowExecutionLedger
{
    Task<bool> AddPlannedStepAsync(WorkflowStepPlan step, CancellationToken cancellationToken);
    Task<bool> SaveAuthorizationTicketAsync(
        WorkflowAuthorizationTicketDraft ticket,
        CancellationToken cancellationToken);
    Task<bool> TryStartObservationStepAsync(
        WorkflowObservationReservation reservation,
        CancellationToken cancellationToken);
    Task<bool> TryReserveEffectStepAsync(
        WorkflowEffectReservation reservation,
        CancellationToken cancellationToken);
    Task<bool> AbandonPlannedStepAsync(
        WorkflowPlannedStepAbandonment abandonment,
        CancellationToken cancellationToken);
    Task<bool> CompleteStepAsync(WorkflowStepCompletion completion, CancellationToken cancellationToken);
    Task<WorkflowRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken);
    Task<WorkflowStepRunRecord?> GetStepAsync(string toolCallId, CancellationToken cancellationToken);
}

internal sealed class WorkflowExecutionLedger(WorkflowLedgerRepository repository) : IWorkflowExecutionLedger
{
    public Task<bool> AddPlannedStepAsync(WorkflowStepPlan step, CancellationToken cancellationToken) =>
        repository.AddPlannedStepAsync(step, cancellationToken);

    public Task<bool> SaveAuthorizationTicketAsync(
        WorkflowAuthorizationTicketDraft ticket,
        CancellationToken cancellationToken) =>
        repository.SaveAuthorizationTicketAsync(ticket, cancellationToken);

    public Task<bool> TryStartObservationStepAsync(
        WorkflowObservationReservation reservation,
        CancellationToken cancellationToken) =>
        repository.TryStartObservationStepAsync(reservation, cancellationToken);

    public Task<bool> TryReserveEffectStepAsync(
        WorkflowEffectReservation reservation,
        CancellationToken cancellationToken) =>
        repository.TryReserveEffectStepAsync(reservation, cancellationToken);

    public Task<bool> AbandonPlannedStepAsync(
        WorkflowPlannedStepAbandonment abandonment,
        CancellationToken cancellationToken) =>
        repository.AbandonPlannedStepAsync(abandonment, cancellationToken);

    public Task<bool> CompleteStepAsync(
        WorkflowStepCompletion completion,
        CancellationToken cancellationToken) =>
        repository.CompleteStepAsync(completion, cancellationToken);

    public Task<WorkflowRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
        repository.GetRunAsync(runId, cancellationToken);

    public Task<WorkflowStepRunRecord?> GetStepAsync(string toolCallId, CancellationToken cancellationToken) =>
        repository.GetStepAsync(toolCallId, cancellationToken);
}
