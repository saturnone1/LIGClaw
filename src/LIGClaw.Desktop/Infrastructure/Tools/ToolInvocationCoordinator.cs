using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Tools;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var authorization = policy.Prepare(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            invocation.Risk);
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
            var approvalScope = activeGrant is not null
                ? $"always:{activeGrant.Id}"
                : choice switch
                {
                    ToolApprovalChoice.AllowAlways => "always:new",
                    ToolApprovalChoice.AllowConversation => "conversation",
                    _ => "once",
                };
            await RecordApprovalAsync(invocation, approved, approvalScope, preview.Prompt.Action, cancellationToken).ConfigureAwait(false);
            if (!authorization.Allowed) toolHost.DiscardApproval(invocation);

            if (authorization.Allowed)
            {
                var execution = await ExecuteAndRecordAsync(invocation, cancellationToken).ConfigureAwait(false);
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

        return await ExecuteAndRecordAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WindowsToolExecutionResult> ExecuteAndRecordAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken)
    {
        var execution = await toolHost.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        var status = execution.Success
            ? "succeeded"
            : cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
        await RecordExecutionAsync(
            invocation,
            execution,
            status,
            execution.ActivitySummary ?? (execution.Success ? "Windows 작업을 완료했어요." : "Windows 작업을 완료하지 못했어요."),
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
        var material = string.Join("\n", invocation.Name, invocation.Risk, prompt.GrantScope ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

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
