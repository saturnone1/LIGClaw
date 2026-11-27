using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

/// <summary>
/// Selects the one mail transport whose exact frozen Connection binding can prepare the workflow
/// effect. A runtime failure never falls through to another transport.
/// </summary>
internal sealed class CompositeMailSendTool : IWorkflowOneShotToolAdapter
{
    private readonly IReadOnlyList<IWorkflowOneShotToolAdapter> transports;

    public CompositeMailSendTool(params IWorkflowOneShotToolAdapter[] transports)
    {
        if (transports is null || transports.Length < 2 || transports.Any(transport => transport is null) ||
            transports.Any(transport =>
                !StringComparer.Ordinal.Equals(transport.Name, Name) ||
                !StringComparer.Ordinal.Equals(transport.Risk, Risk) ||
                transport.RequiredCapabilities != RequiredCapabilities))
            throw new ArgumentException("메일 transport adapter 구성이 올바르지 않습니다.", nameof(transports));
        this.transports = Array.AsReadOnly(transports.ToArray());
    }

    public string Name => "mail.send.v1";
    public string Risk => "R3";
    public WindowsCapability RequiredCapabilities =>
        WindowsCapability.WebAccess | WindowsCapability.CredentialManager;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(50);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(
        IReadOnlyDictionary<string, object?> input) => null;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken) =>
        Task.FromResult(new WindowsToolExecutionResult(
            false,
            new Dictionary<string, object?>(),
            "메일 발송은 고정된 작업 흐름 안에서만 실행할 수 있어요.",
            ErrorCode: "mail.send.context_required"));

    public async Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
        ToolInvokeParams invocation,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var prepared = new List<WorkflowEffectPreparationResult>(transports.Count);
        try
        {
            foreach (var transport in transports)
            {
                prepared.Add(await transport.PrepareWorkflowEffectAsync(
                    invocation,
                    nowUtc,
                    lifetime,
                    cancellationToken).ConfigureAwait(false));
            }

            var selected = prepared.Where(result => result.Success && result.Effect is not null).ToArray();
            if (selected.Length == 1)
            {
                foreach (var unused in prepared.Where(result => !ReferenceEquals(result, selected[0])))
                {
                    if (unused.Effect is not null)
                        await unused.Effect.DisposeAsync().ConfigureAwait(false);
                }
                return selected[0];
            }

            foreach (var result in prepared)
            {
                if (result.Effect is not null)
                    await result.Effect.DisposeAsync().ConfigureAwait(false);
            }
            return new WorkflowEffectPreparationResult(
                false,
                Error: selected.Length > 1
                    ? "메일 transport binding이 하나로 확정되지 않았어요."
                    : "등록된 메일 연결을 확인하지 못했어요.",
                ErrorCode: selected.Length > 1
                    ? "mail.send.binding_ambiguous"
                    : SelectFailureCode(prepared));
        }
        catch
        {
            foreach (var result in prepared)
            {
                if (result.Effect is null) continue;
                try
                {
                    await result.Effect.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private static string SelectFailureCode(
        IReadOnlyList<WorkflowEffectPreparationResult> results)
    {
        var commonFailure = results
            .Select(result => result.ErrorCode)
            .FirstOrDefault(errorCode => errorCode is not null &&
                !errorCode.StartsWith("mail.gateway.", StringComparison.Ordinal) &&
                !errorCode.StartsWith("mail.smtp.", StringComparison.Ordinal));
        if (commonFailure is not null) return commonFailure;
        var exactTransportFailure = results
            .Select(result => result.ErrorCode)
            .FirstOrDefault(errorCode => errorCode is
                "mail.gateway.binding_mismatch" or
                "mail.gateway.credential_unavailable" or
                "mail.smtp.binding_mismatch" or
                "mail.smtp.credential_unavailable");
        return exactTransportFailure ?? "mail.send.binding_unavailable";
    }
}
