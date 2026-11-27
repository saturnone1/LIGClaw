using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record ToolInvocationPreflight(
    bool Allowed,
    string? Error = null,
    string? AuditSummary = null)
{
    public static ToolInvocationPreflight Allow { get; } = new(true);

    public static ToolInvocationPreflight Deny(string error, string auditSummary) =>
        new(false, error, auditSummary);
}

internal sealed record WorkflowPreparedAuthorization(
    bool Allowed,
    string? Error = null);

internal sealed class ToolInvocationCoordinator
{
    private static readonly TimeSpan AlwaysGrantLifetime = TimeSpan.FromDays(30);
    private readonly ToolInvocationPolicy policy;
    private readonly WindowsToolHost toolHost;
    private readonly Func<WindowsToolApprovalPrompt, bool, CancellationToken, Task<ToolApprovalChoice>> requestApproval;
    private readonly IToolAuditSink? auditSink;
    private readonly ICapabilityGrantStore? grantStore;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> conversationGrants = new(StringComparer.Ordinal);

    internal ToolInvocationCoordinator(
        ToolInvocationPolicy policy,
        WindowsToolHost toolHost,
        Func<WindowsToolApprovalPrompt, CancellationToken, Task<bool>> requestApproval,
        IToolAuditSink? auditSink = null,
        ICapabilityGrantStore? grantStore = null)
        : this(
            policy,
            toolHost,
            async (prompt, _, cancellationToken) =>
                await requestApproval(prompt, cancellationToken).ConfigureAwait(false)
                    ? ToolApprovalChoice.AllowOnce
                    : ToolApprovalChoice.Deny,
            auditSink,
            grantStore)
    {
    }

    internal ToolInvocationCoordinator(
        ToolInvocationPolicy policy,
        WindowsToolHost toolHost,
        Func<WindowsToolApprovalPrompt, bool, CancellationToken, Task<ToolApprovalChoice>> requestApproval,
        IToolAuditSink? auditSink = null,
        ICapabilityGrantStore? grantStore = null)
    {
        this.policy = policy;
        this.toolHost = toolHost;
        this.requestApproval = requestApproval;
        this.auditSink = auditSink;
        this.grantStore = grantStore ?? auditSink as ICapabilityGrantStore;
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(invocation, workflowOnlyObservation: false, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<WindowsToolExecutionResult> ExecuteWorkflowObservationAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(invocation, workflowOnlyObservation: true, cancellationToken)
            .ConfigureAwait(false);

    private async Task<WindowsToolExecutionResult> ExecuteAsync(
        ToolInvokeParams invocation,
        bool workflowOnlyObservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var preparation = policy.Prepare(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            invocation.Risk);
        if (!preparation.Allowed && !preparation.RequiresApproval)
        {
            var failure = Failure(preparation.Error ?? "이 동작을 실행할 수 없어요.");
            await RecordExecutionAsync(
                invocation,
                failure,
                "denied",
                "작업 도구 실행 예약 정책이 요청을 거부했어요.",
                cancellationToken).ConfigureAwait(false);
            return failure;
        }

        return await ExecutePreparedCoreAsync(
            invocation,
            ToolInvocationPreflight.Allow,
            workflowOnlyObservation,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<WorkflowPreparedAuthorization> AuthorizePreparedWorkflowEffectAsync(
        ToolInvokeParams invocation,
        IPreparedWorkflowEffect effect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(effect);
        if (!StringComparer.Ordinal.Equals(invocation.Name, effect.ToolName) ||
            !StringComparer.Ordinal.Equals(invocation.Risk, effect.Risk))
            return new WorkflowPreparedAuthorization(false, "준비된 작업 흐름 동작과 작업 도구 요청이 일치하지 않습니다.");
        var preparation = policy.Prepare(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            invocation.Risk);
        if (!preparation.Allowed && !preparation.RequiresApproval)
            return new WorkflowPreparedAuthorization(false, preparation.Error ?? "작업 흐름 동작을 승인할 수 없어요.");
        var authorization = policy.ClaimPrepared(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            invocation.Risk);
        if (!authorization.Allowed && !authorization.RequiresApproval)
            return new WorkflowPreparedAuthorization(false, authorization.Error ?? "작업 흐름 동작을 승인할 수 없어요.");
        if (!authorization.RequiresApproval) return new WorkflowPreparedAuthorization(authorization.Allowed);

        ToolApprovalChoice choice;
        try
        {
            choice = await requestApproval(effect.ApprovalPrompt, false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            choice = ToolApprovalChoice.Deny;
        }
        var approved = choice == ToolApprovalChoice.AllowOnce;
        authorization = policy.ResolveApproval(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            approved);
        await RecordApprovalAsync(
            invocation,
            authorization.Allowed,
            AddCapabilityAuditScope("workflow_once", invocation.CapabilityContext),
            "작업 흐름의 일회성 동작 승인",
            cancellationToken).ConfigureAwait(false);
        return authorization.Allowed
            ? new WorkflowPreparedAuthorization(true)
            : new WorkflowPreparedAuthorization(false, authorization.Error ?? "작업 흐름 동작을 승인하지 않았어요.");
    }

    internal async Task<WindowsToolExecutionResult> ExecutePreparedAsync(
        ToolInvokeParams invocation,
        ToolInvocationPreflight preflight,
        CancellationToken cancellationToken = default) =>
        await ExecutePreparedCoreAsync(
            invocation,
            preflight,
            workflowOnlyObservation: false,
            cancellationToken).ConfigureAwait(false);

    private async Task<WindowsToolExecutionResult> ExecutePreparedCoreAsync(
        ToolInvokeParams invocation,
        ToolInvocationPreflight preflight,
        bool workflowOnlyObservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(preflight);
        var authorization = policy.ClaimPrepared(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            invocation.Risk);
        if (!authorization.Allowed && !authorization.RequiresApproval)
        {
            var failure = Failure(authorization.Error ?? "이 동작을 실행할 수 없어요.");
            await RecordExecutionAsync(
                invocation,
                failure,
                "denied",
                "예약된 작업 도구 실행을 이어서 처리하지 않았어요.",
                cancellationToken).ConfigureAwait(false);
            return failure;
        }
        if (!preflight.Allowed)
        {
            if (authorization.RequiresApproval)
            {
                _ = policy.ResolveApproval(
                    invocation.ConversationId,
                    invocation.RunId,
                    invocation.ToolCallId,
                    approved: false);
            }
            var failure = Failure(preflight.Error ?? "기능 정책에 따라 실행하지 않았어요.");
            await RecordExecutionAsync(
                invocation,
                failure,
                "denied",
                preflight.AuditSummary ?? "기능 실행 전 정책이 요청을 거부했어요.",
                cancellationToken).ConfigureAwait(false);
            return failure;
        }

        if (authorization.RequiresApproval)
        {
            var preview = toolHost.CreateApprovalPrompt(invocation);
            if (!preview.Success || preview.Prompt is null)
            {
                toolHost.DiscardApproval(invocation);
                _ = policy.ResolveApproval(
                    invocation.ConversationId,
                    invocation.RunId,
                    invocation.ToolCallId,
                    approved: false);
                var failure = Failure(preview.Error ?? "실행할 동작을 확인하지 못했어요.");
                await RecordExecutionAsync(invocation, failure, "denied", "실행 정보를 확인하지 못했어요.", cancellationToken)
                    .ConfigureAwait(false);
                return failure;
            }

            var scopeHash = ComputeScopeHash(invocation, preview.Prompt);
            var allowAlways = StringComparer.Ordinal.Equals(invocation.Risk, "R1") &&
                              grantStore is not null &&
                              !string.IsNullOrWhiteSpace(preview.Prompt.GrantScope);
            var activeGrant = allowAlways
                ? await FindActiveGrantAsync(invocation, scopeHash, cancellationToken).ConfigureAwait(false)
                : null;
            var conversationGrantKey = $"{invocation.ConversationId}\n{scopeHash}";
            var hasConversationGrant = allowAlways && conversationGrants.ContainsKey(conversationGrantKey);
            var choice = activeGrant is not null
                ? ToolApprovalChoice.AllowAlways
                : hasConversationGrant ? ToolApprovalChoice.AllowConversation : ToolApprovalChoice.Deny;
            if (activeGrant is null && !hasConversationGrant)
            {
                try
                {
                    choice = await requestApproval(preview.Prompt, allowAlways, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    choice = ToolApprovalChoice.Deny;
                }
            }
            var approved = choice is ToolApprovalChoice.AllowOnce or ToolApprovalChoice.AllowConversation or ToolApprovalChoice.AllowAlways;
            authorization = policy.ResolveApproval(
                invocation.ConversationId,
                invocation.RunId,
                invocation.ToolCallId,
                approved);
            var approvalScopeKind = activeGrant is not null
                ? $"always:{activeGrant.Id}"
                : choice switch
                {
                    ToolApprovalChoice.AllowAlways => "always:new",
                    ToolApprovalChoice.AllowConversation => "conversation",
                    _ => "once",
                };
            var approvalScope = AddCapabilityAuditScope(
                approvalScopeKind,
                invocation.CapabilityContext);
            await RecordApprovalAsync(invocation, approved, approvalScope, preview.Prompt.Action, cancellationToken).ConfigureAwait(false);
            if (!authorization.Allowed) toolHost.DiscardApproval(invocation);

            if (authorization.Allowed)
            {
                var execution = await ExecuteAndRecordAsync(
                    invocation,
                    workflowOnlyObservation,
                    cancellationToken).ConfigureAwait(false);
                if (execution.Success && choice == ToolApprovalChoice.AllowConversation && !hasConversationGrant && allowAlways)
                    conversationGrants[conversationGrantKey] = 0;
                if (execution.Success && choice == ToolApprovalChoice.AllowAlways && activeGrant is null && allowAlways)
                    await SaveGrantAsync(invocation, scopeHash, preview.Prompt.Action, cancellationToken).ConfigureAwait(false);
                return execution;
            }
        }

        if (!authorization.Allowed)
        {
            var failure = Failure(authorization.Error ?? "이 동작을 실행할 수 없어요.");
            await RecordExecutionAsync(invocation, failure, "denied", "정책 또는 사용자 결정으로 실행하지 않았어요.", cancellationToken)
                .ConfigureAwait(false);
            return failure;
        }

        return await ExecuteAndRecordAsync(invocation, workflowOnlyObservation, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WindowsToolExecutionResult> ExecuteAndRecordAsync(
        ToolInvokeParams invocation,
        bool workflowOnlyObservation,
        CancellationToken cancellationToken)
    {
        var execution = workflowOnlyObservation
            ? await toolHost.ExecuteWorkflowObservationAsync(invocation, cancellationToken).ConfigureAwait(false)
            : await toolHost.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        var status = execution.Success
            ? "succeeded"
            : cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
        await RecordExecutionAsync(
            invocation,
            execution,
            status,
            execution.ActivitySummary ?? (execution.Success ? "윈도우 작업을 완료했어요." : "윈도우 작업을 완료하지 못했어요."),
            cancellationToken).ConfigureAwait(false);
        return execution;
    }

    private async Task RecordApprovalAsync(
        ToolInvokeParams invocation,
        bool approved,
        string scope,
        string summary,
        CancellationToken cancellationToken)
    {
        if (auditSink is null) return;
        try
        {
            await auditSink.RecordApprovalAsync(
                new ToolApprovalAuditRecord(
                    invocation.ConversationId,
                    invocation.RunId,
                    invocation.ToolCallId,
                    invocation.Name,
                    invocation.Risk,
                    approved,
                    scope,
                    summary,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 감사 저장 실패가 승인된 Windows 작업의 결과를 바꾸지는 않는다.
        }
    }

    private async Task<CapabilityGrant?> FindActiveGrantAsync(
        ToolInvokeParams invocation,
        string scopeHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await grantStore!.FindActiveGrantAsync(
                invocation.Name, invocation.Risk, scopeHash, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task SaveGrantAsync(
        ToolInvokeParams invocation,
        string scopeHash,
        string summary,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await grantStore!.SaveGrantAsync(
                new CapabilityGrant(
                    Guid.NewGuid().ToString("N"), invocation.Name, invocation.Risk, scopeHash,
                    summary, now, now.Add(AlwaysGrantLifetime)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 권한 저장 실패 시 이번 실행만 승인된 것으로 남긴다.
        }
    }

    private static string ComputeScopeHash(ToolInvokeParams invocation, WindowsToolApprovalPrompt prompt)
    {
        var material = string.Join(
            "\n",
            invocation.Name,
            invocation.Risk,
            prompt.GrantScope ?? string.Empty,
            CapabilityIdentityMaterial(invocation.CapabilityContext));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string AddCapabilityAuditScope(
        string scope,
        CapabilityInvocationContext? context) =>
        $"{scope}|cap:{CapabilityIdentityDigest(context)}";

    private static string CapabilityIdentityDigest(CapabilityInvocationContext? context)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CapabilityIdentityMaterial(context)));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string CapabilityIdentityMaterial(CapabilityInvocationContext? context) =>
        context is null
            ? "none"
            : string.Join(
                "\n",
                context.SnapshotHash,
                context.ModelToolName,
                context.CapabilityId,
                context.Version,
                context.ContentHash,
                context.CatalogRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                context.ConnectionBindingHash);

    private async Task RecordExecutionAsync(
        ToolInvokeParams invocation,
        WindowsToolExecutionResult execution,
        string status,
        string summary,
        CancellationToken cancellationToken)
    {
        if (auditSink is null) return;
        try
        {
            await auditSink.RecordExecutionAsync(
                new ToolExecutionAuditRecord(
                    invocation.ConversationId,
                    invocation.RunId,
                    invocation.ToolCallId,
                    invocation.Name,
                    invocation.Risk,
                    status,
                    summary,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 감사 저장 실패는 별도 진단 대상이며 Tool 결과에는 민감한 저장 오류를 싣지 않는다.
        }
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
