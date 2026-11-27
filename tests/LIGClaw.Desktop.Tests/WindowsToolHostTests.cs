using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class WindowsToolHostTests
{
    [Theory]
    [InlineData(19045, "Windows 10")]
    [InlineData(22631, "Windows 11 이상")]
    public async Task ExecutesSystemStatusThroughTheMatchingPlatformAdapter(int build, string release)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);
        var host = new WindowsToolHost(profile);

        var result = await host.ExecuteAsync(Invocation("system.get_status.v1", "R0"));

        Assert.True(result.Success);
        Assert.Equal(release, result.Output["windowsRelease"]);
        Assert.Equal((long)build, result.Output["buildNumber"]);
    }

    [Theory]
    [InlineData(19045)]
    [InlineData(22631)]
    public async Task ListsWindowsThroughTheCommonWindows10And11Adapter(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);
        var catalog = new FakeWindowCatalog(
        [
            new WindowCatalogEntry("0x2", "Notes", "notepad", false, true),
            new WindowCatalogEntry("0x1", "Inbox", "mail", true, false),
        ]);
        var host = new WindowsToolHost(profile, [new AppListWindowsTool(catalog)]);

        var result = await host.ExecuteAsync(Invocation("app.list_windows.v1", "R0"));

        Assert.True(result.Success);
        var windows = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.Output["windows"]);
        Assert.Equal(2, windows.Count);
        Assert.Equal("0x1", windows[0]["windowId"]);
        Assert.Equal("mail", windows[0]["processName"]);
        Assert.Equal(true, windows[0]["isForeground"]);
        Assert.Equal(true, windows[1]["isMinimized"]);
    }

    [Fact]
    public async Task BoundsTheReturnedWindowList()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var catalog = new FakeWindowCatalog(Enumerable.Range(0, 120)
            .Select(index => new WindowCatalogEntry($"0x{index:X}", $"Window {index}", "fixture", false, false))
            .ToArray());
        var host = new WindowsToolHost(profile, [new AppListWindowsTool(catalog)]);

        var result = await host.ExecuteAsync(Invocation("app.list_windows.v1", "R0"));

        var windows = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(result.Output["windows"]);
        Assert.Equal(100, windows.Count);
    }

    [Theory]
    [InlineData(19045)]
    [InlineData(22631)]
    public async Task PreviewsAndShowsAnApprovedNotification(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);
        var notifications = new FakeNotificationService();
        var host = new WindowsToolHost(profile, [new SystemShowNotificationTool(notifications)]);
        var input = new Dictionary<string, object?>
        {
            ["title"] = "회의 알림",
            ["message"] = "10분 뒤 회의가 시작됩니다.",
            ["reason"] = "사용자가 회의 알림을 요청했기 때문에",
        };
        var invocation = new ToolInvokeParams(
            "call-1", "conversation-1", "run-1", "system.show_notification.v1", "R1", input);

        var preview = host.CreateApprovalPrompt(invocation);
        var execution = await host.ExecuteAsync(invocation);

        Assert.True(preview.Success);
        Assert.NotNull(preview.Prompt);
        Assert.Contains("회의 알림", preview.Prompt.Action, StringComparison.Ordinal);
        Assert.Contains("사용자가 회의 알림을 요청", preview.Prompt.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("system.show_notification", preview.Prompt.Action, StringComparison.Ordinal);
        Assert.Equal("R1", preview.Prompt.Risk);
        Assert.False(preview.Prompt.CanUndo);
        Assert.True(execution.Success);
        Assert.Equal(("회의 알림", "10분 뒤 회의가 시작됩니다."), Assert.Single(notifications.Shown));
    }

    [Fact]
    public async Task RejectsMalformedNotificationInputBeforeShowingIt()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var notifications = new FakeNotificationService();
        var host = new WindowsToolHost(profile, [new SystemShowNotificationTool(notifications)]);
        var invocation = new ToolInvokeParams(
            "call-1",
            "conversation-1",
            "run-1",
            "system.show_notification.v1",
            "R1",
            new Dictionary<string, object?> { ["title"] = "제목만 있음" });

        Assert.False(host.CreateApprovalPrompt(invocation).Success);
        Assert.False((await host.ExecuteAsync(invocation)).Success);
        Assert.Empty(notifications.Shown);
    }

    [Theory]
    [InlineData(19045)]
    [InlineData(22631)]
    public async Task LaunchesOnlyTheCanonicalAppShownInTheApprovalPreview(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);
        var app = new RegisteredAppEntry("start-menu:calculator", "Calculator", "C:\\Start Menu\\Calculator.lnk");
        var catalog = new FakeRegisteredAppCatalog(_ => app);
        var launcher = new FakeRegisteredAppLauncher();
        var host = new WindowsToolHost(profile, [new AppLaunchTool(catalog, launcher)]);
        var invocation = AppLaunchInvocation("Calculator");

        var preview = host.CreateApprovalPrompt(invocation);
        var execution = await host.ExecuteAsync(invocation);

        Assert.True(preview.Success);
        var prompt = Assert.IsType<WindowsToolApprovalPrompt>(preview.Prompt);
        Assert.Contains("Calculator 앱을 엽니다", prompt.Action, StringComparison.Ordinal);
        Assert.Equal("registered-app:start-menu:calculator", prompt.GrantScope);
        Assert.True(execution.Success);
        Assert.Equal(app, Assert.Single(launcher.Launched));
        Assert.Equal("Calculator", execution.Output["displayName"]);
    }

    [Fact]
    public async Task RejectsAnAppWhoseRegistrationChangesAfterApproval()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var resolutionCount = 0;
        var catalog = new FakeRegisteredAppCatalog(_ => ++resolutionCount == 1
            ? new RegisteredAppEntry("original", "Calculator", "C:\\Start Menu\\Calculator.lnk")
            : new RegisteredAppEntry("changed", "Calculator", "C:\\Other\\Calculator.lnk"));
        var launcher = new FakeRegisteredAppLauncher();
        var host = new WindowsToolHost(profile, [new AppLaunchTool(catalog, launcher)]);
        var invocation = AppLaunchInvocation("Calculator");

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var execution = await host.ExecuteAsync(invocation);

        Assert.False(execution.Success);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task DoesNotLaunchAfterTheApprovalIsRejected()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var app = new RegisteredAppEntry("calculator", "Calculator", "C:\\Start Menu\\Calculator.lnk");
        var launcher = new FakeRegisteredAppLauncher();
        var host = new WindowsToolHost(
            profile,
            [new AppLaunchTool(new FakeRegisteredAppCatalog(_ => app), launcher)]);
        var invocation = AppLaunchInvocation("Calculator");

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        host.DiscardApproval(invocation);
        var execution = await host.ExecuteAsync(invocation);

        Assert.False(execution.Success);
        Assert.Empty(launcher.Launched);
    }

    [Theory]
    [InlineData("app.activate.v1", "R1")]
    [InlineData("app.close.v1", "R2")]
    public async Task WindowActionsRevalidateTheCanonicalTarget(string toolName, string risk)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var target = new WindowActionTarget("0x123", "Fixture", "fixture");
        var windows = new FakeWindowActionService(_ => target);
        IWindowsToolAdapter adapter = toolName == "app.activate.v1"
            ? new AppActivateTool(windows)
            : new AppCloseTool(windows);
        var host = new WindowsToolHost(profile, [adapter]);
        var invocation = WindowActionInvocation(toolName, risk);

        var preview = host.CreateApprovalPrompt(invocation);
        var execution = await host.ExecuteAsync(invocation);

        Assert.True(preview.Success);
        Assert.True(execution.Success);
        Assert.Equal(toolName == "app.activate.v1" ? 1 : 0, windows.Activated.Count);
        Assert.Equal(toolName == "app.close.v1" ? 1 : 0, windows.Closed.Count);
    }

    [Fact]
    public async Task WindowActionRefusesAReusedWindowIdAfterApproval()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var resolutionCount = 0;
        var windows = new FakeWindowActionService(_ => ++resolutionCount == 1
            ? new WindowActionTarget("0x123", "Original", "original")
            : new WindowActionTarget("0x123", "Replacement", "replacement"));
        var host = new WindowsToolHost(profile, [new AppCloseTool(windows)]);
        var invocation = WindowActionInvocation("app.close.v1", "R2");

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var execution = await host.ExecuteAsync(invocation);

        Assert.False(execution.Success);
        Assert.Empty(windows.Closed);
    }

    [Fact]
    public async Task RejectsUnknownToolsAndRiskDowngrades()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var host = new WindowsToolHost(profile);

        Assert.False((await host.ExecuteAsync(Invocation("unknown.v1", "R0"))).Success);
        Assert.False((await host.ExecuteAsync(Invocation("system.get_status.v1", "R1"))).Success);
    }

    [Fact]
    public async Task SelectsTheHighestPriorityCompatibleAdapter()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var host = new WindowsToolHost(profile,
        [
            new FakeTool(WindowsCapability.Win32DesktopShell, priority: 1, "common"),
            new FakeTool(WindowsCapability.Windows11Shell, priority: 10, "windows11"),
        ]);

        var result = await host.ExecuteAsync(Invocation("fake.v1", "R0"));

        Assert.Equal("windows11", result.Output["adapter"]);
    }

    [Fact]
    public void RegistryRejectsAnAmbiguousDuplicateAdapter()
    {
        var adapter = new FakeTool(WindowsCapability.Win32DesktopShell, 1, "duplicate");

        Assert.Throws<InvalidOperationException>(() => new WindowsToolRegistry([adapter, adapter]));
    }

    [Fact]
    public async Task ExecutesAnAuthorizedHigherRiskToolWithoutOwningApprovalPolicy()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var host = new WindowsToolHost(profile, [new FakeTool(WindowsCapability.Win32DesktopShell, 1, "approved", "R1")]);

        var result = await host.ExecuteAsync(Invocation("fake.v1", "R1"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task EnforcesTheDesktopAdapterTimeout()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var host = new WindowsToolHost(profile, [new TimeoutTool()]);

        var result = await host.ExecuteAsync(Invocation("timeout.v1", "R0"));

        Assert.False(result.Success);
        Assert.Contains("제한 시간", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PropagatesCallerCancellationToTheDesktopAdapter()
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var host = new WindowsToolHost(profile, [new TimeoutTool()]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await host.ExecuteAsync(Invocation("timeout.v1", "R0"), cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("중단", result.Error, StringComparison.Ordinal);
    }

    private static ToolInvokeParams Invocation(string name, string risk) =>
        new("call-1", "conversation-1", "run-1", name, risk, new Dictionary<string, object?>());

    private static ToolInvokeParams AppLaunchInvocation(string appName) =>
        new(
            "call-app",
            "conversation-1",
            "run-1",
            "app.launch.v1",
            "R1",
            new Dictionary<string, object?>
            {
                ["appName"] = appName,
                ["reason"] = "사용자가 요청했기 때문에",
            });

    private static ToolInvokeParams WindowActionInvocation(string name, string risk) =>
        new(
            "call-window",
            "conversation-1",
            "run-1",
            name,
            risk,
            new Dictionary<string, object?>
            {
                ["windowId"] = "0x123",
                ["reason"] = "사용자가 요청했기 때문에",
            });

    private sealed class FakeTool(
        WindowsCapability requiredCapabilities,
        int priority,
        string result,
        string risk = "R0") : IWindowsToolAdapter
    {
        public string Name => "fake.v1";
        public string Risk => risk;
        public WindowsCapability RequiredCapabilities => requiredCapabilities;
        public int Priority => priority;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?> { ["adapter"] = result }));
    }

    private sealed class TimeoutTool : IWindowsToolAdapter
    {
        public string Name => "timeout.v1";
        public string Risk => "R0";
        public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
        public int Priority => 0;
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(10);
        public async Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FakeWindowCatalog(IReadOnlyList<WindowCatalogEntry> entries) : IWindowCatalog
    {
        public Task<IReadOnlyList<WindowCatalogEntry>> ListWindowsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(entries);
        }
    }

    private sealed class FakeNotificationService : IUserNotificationService
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public Task ShowAsync(string title, string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Shown.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRegisteredAppCatalog(Func<string, RegisteredAppEntry?> resolve) : IRegisteredAppCatalog
    {
        public RegisteredAppEntry? Resolve(string appName) => resolve(appName);
    }

    private sealed class FakeRegisteredAppLauncher : IRegisteredAppLauncher
    {
        public List<RegisteredAppEntry> Launched { get; } = [];

        public Task LaunchAsync(RegisteredAppEntry app, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launched.Add(app);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWindowActionService(Func<string, WindowActionTarget?> resolve) : IWindowActionService
    {
        public List<WindowActionTarget> Activated { get; } = [];
        public List<WindowActionTarget> Closed { get; } = [];

        public Task<WindowActionTarget?> ResolveAsync(string windowId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(resolve(windowId));
        }

        public Task<bool> ActivateAsync(WindowActionTarget target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Activated.Add(target);
            return Task.FromResult(true);
        }

        public Task<bool> CloseAsync(WindowActionTarget target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Closed.Add(target);
            return Task.FromResult(true);
        }
    }
}
