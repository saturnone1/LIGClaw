using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class EdgeBrowserToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ligclaw-edge-{Guid.NewGuid():N}");

    [Fact]
    public async Task OpensOnlyANewEdgeWindowWithAnIsolatedProfileAndExpiringHandle()
    {
        Directory.CreateDirectory(_directory);
        var executable = Path.Combine(_directory, "msedge.exe");
        await File.WriteAllTextAsync(executable, string.Empty);
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var windows = new SequencedWindowCatalog(
            [],
            [new WindowCatalogEntry("0xABC", "Intranet", "msedge", false, false)]);
        var automation = new FakeAutomation
        {
            Window = new UiAutomationWindowTarget("0xABC", 42, 1234, "Intranet", "msedge"),
        };
        var launcher = new FakeEdgeLauncher();
        var service = new EdgeBrowserSessionService(
            windows, automation, launcher, () => executable, () => now, () => Path.Combine(_directory, "profile"));

        var session = await service.OpenAsync(new Uri("http://10.0.0.20/wiki#section"), CancellationToken.None);

        Assert.Equal("0xABC", session.WindowId);
        Assert.Equal(32, session.Handle.Length);
        Assert.Equal(now.AddMinutes(30), session.ExpiresAtUtc);
        Assert.Equal("http://10.0.0.20/wiki", launcher.Uri!.ToString());
        Assert.Equal(Path.Combine(_directory, "profile"), launcher.ProfilePath);
        Assert.True(Directory.Exists(launcher.ProfilePath));
    }

    [Fact]
    public async Task SnapshotUsesOnlyDesktopIssuedLiveHandleAndReturnsReadOnlyElements()
    {
        Directory.CreateDirectory(_directory);
        var executable = Path.Combine(_directory, "msedge.exe");
        await File.WriteAllTextAsync(executable, string.Empty);
        var windows = new SequencedWindowCatalog(
            [],
            [new WindowCatalogEntry("0xABC", "Docs", "msedge", false, false)]);
        var automation = new FakeAutomation
        {
            Window = new UiAutomationWindowTarget("0xABC", 42, 1234, "Docs", "msedge"),
            Inspection = new UiAutomationInspection(
                "0xABC", "Docs", "msedge",
                [new UiAutomationElementSummary("secret-action-handle", "문서 제목", "", "Text", true, false, false, false, false)],
                false),
        };
        var service = new EdgeBrowserSessionService(
            windows, automation, new FakeEdgeLauncher(), () => executable,
            profilePath: () => Path.Combine(_directory, "profile"));
        var session = await service.OpenAsync(new Uri("https://docs.example/page"), CancellationToken.None);
        var tool = new BrowserSnapshotTool(service);
        var input = new Dictionary<string, object?>
        {
            ["browserHandle"] = session.Handle,
            ["query"] = "문서",
            ["maxElements"] = 20,
            ["reason"] = "사용자가 페이지 내용을 요청했기 때문에",
        };

        var preview = tool.CreateApprovalPrompt(input);
        var result = await tool.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.True(result.Success);
        Assert.Equal(20, automation.MaximumElements);
        var element = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.Output["elements"]));
        Assert.Equal("문서 제목", element["name"]);
        Assert.DoesNotContain("elementId", element.Keys);
        Assert.DoesNotContain("secret-action-handle", System.Text.Json.JsonSerializer.Serialize(result.Output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsExpiredOrIdentityChangedHandle()
    {
        Directory.CreateDirectory(_directory);
        var executable = Path.Combine(_directory, "msedge.exe");
        await File.WriteAllTextAsync(executable, string.Empty);
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var automation = new FakeAutomation
        {
            Window = new UiAutomationWindowTarget("0xABC", 42, 1234, "Docs", "msedge"),
        };
        var service = new EdgeBrowserSessionService(
            new SequencedWindowCatalog([], [new WindowCatalogEntry("0xABC", "Docs", "msedge", false, false)]),
            automation, new FakeEdgeLauncher(), () => executable, () => now,
            () => Path.Combine(_directory, "profile"));
        var session = await service.OpenAsync(new Uri("https://docs.example"), CancellationToken.None);

        automation.Window = automation.Window with { ProcessStartedAtUtcTicks = 9999 };
        Assert.Null(await service.GetSessionAsync(session.Handle, CancellationToken.None));
        Assert.Null(await service.GetSessionAsync(new string('a', 32), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class SequencedWindowCatalog(params IReadOnlyList<WindowCatalogEntry>[] results) : IWindowCatalog
    {
        private int _index;
        public Task<IReadOnlyList<WindowCatalogEntry>> ListWindowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(results[Math.Min(_index++, results.Length - 1)]);
    }

    private sealed class FakeEdgeLauncher : IEdgeProcessLauncher
    {
        public string? ProfilePath { get; private set; }
        public Uri? Uri { get; private set; }
        public void Launch(string executablePath, string profilePath, Uri uri)
        {
            ProfilePath = profilePath;
            Uri = uri;
        }
    }

    private sealed class FakeAutomation : IUiAutomationService
    {
        public UiAutomationWindowTarget? Window { get; set; }
        public UiAutomationInspection? Inspection { get; set; }
        public int MaximumElements { get; private set; }

        public Task<UiAutomationWindowTarget?> ResolveWindowAsync(string windowId, CancellationToken cancellationToken) =>
            Task.FromResult(Window);

        public Task<UiAutomationInspection?> InspectAsync(
            string windowId, string? query, int maximumElements, CancellationToken cancellationToken)
        {
            MaximumElements = maximumElements;
            return Task.FromResult(Inspection);
        }

        public Task<UiAutomationElementTarget?> ResolveAsync(string elementId, CancellationToken cancellationToken) =>
            Task.FromResult<UiAutomationElementTarget?>(null);
        public Task<UiAutomationActionStatus> InvokeAsync(UiAutomationElementTarget expected, CancellationToken cancellationToken) =>
            Task.FromResult(UiAutomationActionStatus.Unsupported);
        public Task<UiAutomationActionStatus> SetValueAsync(UiAutomationElementTarget expected, string value, CancellationToken cancellationToken) =>
            Task.FromResult(UiAutomationActionStatus.Unsupported);
        public Task<UiAutomationActionStatus> SendTextAsync(UiAutomationElementTarget expected, string text, CancellationToken cancellationToken) =>
            Task.FromResult(UiAutomationActionStatus.Unsupported);
    }
}
