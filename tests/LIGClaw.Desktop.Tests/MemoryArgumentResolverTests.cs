using LIGClaw.Application.Memory;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class MemoryArgumentResolverTests
{
    [Fact]
    public async Task PinsTheResolvedAliasShownInTheApprovalPreview()
    {
        var memories = new MutableMemoryRepository("C:\\Approved");
        var adapter = new CapturingPathAdapter();
        var host = new WindowsToolHost(Profile(), [adapter], memories: memories);
        var invocation = new LIGClaw.Contracts.Generated.ToolInvokeParams(
            "call-alias", "conversation", "run", "fixture.path.v1", "R1",
            new Dictionary<string, object?> { ["path"] = "$alias:주 프로젝트" });

        var preview = host.CreateApprovalPrompt(invocation);
        memories.Value = "C:\\ChangedAfterApproval";
        var result = await host.ExecuteAsync(invocation);

        Assert.True(preview.Success);
        Assert.Contains("C:\\Approved", preview.Prompt!.Details, StringComparison.Ordinal);
        Assert.True(result.Success);
        Assert.Equal("C:\\Approved", adapter.ExecutedPath);
    }

    [Fact]
    public void RejectsAMissingAliasBeforeApproval()
    {
        var host = new WindowsToolHost(Profile(), [new CapturingPathAdapter()], memories: new MutableMemoryRepository(null));
        var invocation = new LIGClaw.Contracts.Generated.ToolInvokeParams(
            "call-missing", "conversation", "run", "fixture.path.v1", "R1",
            new Dictionary<string, object?> { ["path"] = "$alias:없는 별칭" });

        var result = host.CreateApprovalPrompt(invocation);

        Assert.False(result.Success);
        Assert.Contains("별칭", result.Error, StringComparison.Ordinal);
    }

    private static WindowsPlatformProfile Profile() =>
        WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);

    private sealed class CapturingPathAdapter : IWindowsToolAdapter
    {
        public string Name => "fixture.path.v1";
        public string Risk => "R1";
        public WindowsCapability RequiredCapabilities => WindowsCapability.None;
        public int Priority => 0;
        public string? ExecutedPath { get; private set; }

        public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input) =>
            new("경로 사용", "승인된 경로를 사용합니다.", input["path"]?.ToString() ?? string.Empty);

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            ExecutedPath = input["path"]?.ToString();
            return Task.FromResult(new WindowsToolExecutionResult(true, new Dictionary<string, object?>()));
        }
    }

    private sealed class MutableMemoryRepository(string? value) : IMemoryRepository
    {
        public string? Value { get; set; } = value;
        public Task<string?> ResolveValueAsync(string kind, string key, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Value);
        public Task<MemoryUpsertResult> UpsertAsync(PersonalMemoryDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalMemory>> ListAsync(string? query, int limit, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PersonalMemory?> GetAsync(string memoryId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalMemory>> ListForManagementAsync(string? query, int offset, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PersonalMemory?> GetForManagementAsync(string memoryId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
