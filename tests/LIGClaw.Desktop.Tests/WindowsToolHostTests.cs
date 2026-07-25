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

    private static ToolInvokeParams Invocation(string name, string risk) =>
        new("call-1", "conversation-1", "run-1", name, risk, new Dictionary<string, object?>());

    private sealed class FakeTool(
        WindowsCapability requiredCapabilities,
        int priority,
        string result) : IWindowsToolAdapter
    {
        public string Name => "fake.v1";
        public string Risk => "R0";
        public WindowsCapability RequiredCapabilities => requiredCapabilities;
        public int Priority => priority;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?> { ["adapter"] = result }));
    }
}
