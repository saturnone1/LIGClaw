using LIGClaw.Application.Memory;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class MemoryToolTests
{
    [Fact]
    public async Task RememberRequiresApprovalAndUpsertsWithoutAuditingTheValue()
    {
        var repository = new FakeMemoryRepository();
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");
        var host = new WindowsToolHost(Profile(), [new MemoryRememberTool(repository)]);
        var audit = new FakeAuditSink();
        var coordinator = new ToolInvocationCoordinator(policy, host, (prompt, _) =>
        {
            Assert.Contains("private-value", prompt.Details, StringComparison.Ordinal);
            return Task.FromResult(true);
        }, audit);
        var invocation = new ToolInvokeParams(
            "call", "conversation", "run", "memory.remember.v1", "R1",
            new Dictionary<string, object?>
            {
                ["kind"] = "preference",
                ["key"] = "응답 스타일",
                ["value"] = "private-value",
                ["sensitivity"] = "personal",
                ["reason"] = "사용자가 기억해 달라고 요청했기 때문에",
            });

        var result = await coordinator.ExecuteAsync(invocation);

        Assert.True(result.Success);
        Assert.NotNull(host.CreateApprovalPrompt(invocation with { ToolCallId = "scope-check" }).Prompt?.GrantScope);
        Assert.Equal("conversation:conversation", Assert.Single(repository.Stored).Source);
        Assert.DoesNotContain("private-value", Assert.Single(audit.Approvals).Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", Assert.Single(audit.Executions).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RememberRejectsCredentialKeysEvenAfterApproval()
    {
        var repository = new FakeMemoryRepository();
        var host = new WindowsToolHost(Profile(), [new MemoryRememberTool(repository)]);
        var invocation = new ToolInvokeParams(
            "call", "conversation", "run", "memory.remember.v1", "R1",
            new Dictionary<string, object?>
            {
                ["kind"] = "note",
                ["key"] = "API key",
                ["value"] = "secret",
                ["sensitivity"] = "sensitive",
                ["reason"] = "테스트",
            });

        Assert.False(host.CreateApprovalPrompt(invocation).Success);
        Assert.False((await host.ExecuteAsync(invocation)).Success);
        Assert.Empty(repository.Stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task RememberTreatsNullOrZeroOptionalTtlAsNoExpiry(object? ttlDays)
    {
        var repository = new FakeMemoryRepository();
        var tool = new MemoryRememberTool(repository);
        var input = new Dictionary<string, object?>
        {
            ["kind"] = "preference",
            ["key"] = "선호 에디터",
            ["value"] = "VS Code",
            ["sensitivity"] = "personal",
            ["ttlDays"] = ttlDays,
            ["reason"] = "사용자가 기억해 달라고 요청했기 때문에",
        };

        Assert.NotNull(tool.CreateApprovalPrompt(input));
        Assert.True((await tool.ExecuteAsync(input, CancellationToken.None)).Success);
        Assert.Null(Assert.Single(repository.Stored).ExpiresAtUtc);
    }

    private static WindowsPlatformProfile Profile() =>
        WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);

    private sealed class FakeMemoryRepository : IMemoryRepository
    {
        public List<PersonalMemoryDraft> Stored { get; } = [];
        public Task<MemoryUpsertResult> UpsertAsync(PersonalMemoryDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            Stored.Add(draft);
            return Task.FromResult(new MemoryUpsertResult(
                new PersonalMemory(new string('a', 32), draft.Kind, draft.Key, draft.Value, draft.Sensitivity, draft.Source, nowUtc, nowUtc, draft.ExpiresAtUtc),
                true));
        }
        public Task<IReadOnlyList<PersonalMemory>> ListAsync(string? query, int limit, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersonalMemory>>([]);
        public Task<PersonalMemory?> GetAsync(string memoryId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult<PersonalMemory?>(null);
        public Task<IReadOnlyList<PersonalMemory>> ListForManagementAsync(string? query, int offset, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersonalMemory>>([]);
        public Task<PersonalMemory?> GetForManagementAsync(string memoryId, CancellationToken cancellationToken) =>
            Task.FromResult<PersonalMemory?>(null);
        public Task<string?> ResolveValueAsync(string kind, string key, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FakeAuditSink : IToolAuditSink
    {
        public List<ToolApprovalAuditRecord> Approvals { get; } = [];
        public List<ToolExecutionAuditRecord> Executions { get; } = [];
        public Task RecordApprovalAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken)
        {
            Approvals.Add(record);
            return Task.CompletedTask;
        }
        public Task RecordExecutionAsync(ToolExecutionAuditRecord record, CancellationToken cancellationToken)
        {
            Executions.Add(record);
            return Task.CompletedTask;
        }
    }
}
