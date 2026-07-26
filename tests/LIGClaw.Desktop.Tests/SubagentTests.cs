using System.Text.Json;
using LIGClaw.Application.Agents;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class SubagentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LedgerBoundsResultsAndReconcilesOrphansAfterRestart()
    {
        using var store = await CreateStoreAsync();
        var draft = Draft();
        var batchId = Guid.NewGuid().ToString("N");
        var tasks = await store.CreateSubagentBatchAsync(batchId, draft, DateTimeOffset.UtcNow, CancellationToken.None);
        var first = tasks[0];
        await store.CompleteSubagentTaskAsync(
            first.ChildRunId, true, new string('x', 1_200), null, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.ReconcileSubagentTasksOnStartupAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var stored = await store.ListSubagentTasksAsync(batchId, CancellationToken.None);
        Assert.Equal("succeeded", stored[0].Status);
        Assert.Equal(1_001, stored[0].ResultText!.Length);
        Assert.Equal("interrupted", stored[1].Status);
        Assert.Equal("desktop_restart", stored[1].ErrorCode);
    }

    [Fact]
    public async Task ToolCarriesParentIdentityAndReturnsJoinedBoundedResults()
    {
        var orchestrator = new RecordingOrchestrator();
        var tool = new SubagentRunTool(() => orchestrator);
        var input = Input();
        var preview = Assert.IsType<WindowsToolApprovalPrompt>(tool.CreateApprovalPrompt(input));
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22631, true),
            [tool]);

        var result = await host.ExecuteAsync(new ToolInvokeParams(
            "tool-call", "parent-conversation", "parent-run", "subagent.run.v1", "R1", input));

        Assert.Contains("권한 상한: R0", preview.Details, StringComparison.Ordinal);
        Assert.True(result.Success);
        Assert.Equal("parent-conversation", orchestrator.Draft!.ParentConversationId);
        Assert.Equal("parent-run", orchestrator.Draft.ParentRunId);
        Assert.Equal("tool-call", orchestrator.Draft.ParentToolCallId);
        Assert.Equal(2, ((IReadOnlyDictionary<string, object?>[])result.Output["tasks"]!).Length);
    }

    private async Task<ConversationStore> CreateStoreAsync()
    {
        var store = new ConversationStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        await store.InitializeAsync();
        return store;
    }

    private static SubagentBatchDraft Draft() => new(
        "parent", "run", "tool", [new("CPU", "CPU 분석"), new("메모리", "메모리 분석")],
        null, "R0", 120, 1_000, "독립 분석");

    private static Dictionary<string, object?> Input()
    {
        using var document = JsonDocument.Parse("""
        [{"title":"CPU 분석","prompt":"현재 CPU 상태를 분석해 줘."},{"title":"메모리 분석","prompt":"현재 메모리 상태를 분석해 줘."}]
        """);
        return new Dictionary<string, object?>
        {
            ["tasks"] = document.RootElement.Clone(),
            ["maxRisk"] = "R0",
            ["maxRuntimeSeconds"] = 120,
            ["resultMaxCharacters"] = 5_000,
            ["reason"] = "독립 분석을 병렬로 실행하기 위해",
        };
    }

    private sealed class RecordingOrchestrator : ILocalSubagentOrchestrator
    {
        public SubagentBatchDraft? Draft { get; private set; }
        public Task<SubagentBatchResult> ExecuteAsync(SubagentBatchDraft draft, CancellationToken cancellationToken)
        {
            Draft = draft;
            return Task.FromResult(new SubagentBatchResult(
                "f".PadLeft(32, 'f'),
                [new("a", "CPU 분석", true, "정상", null), new("b", "메모리 분석", true, "정상", null)]));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
