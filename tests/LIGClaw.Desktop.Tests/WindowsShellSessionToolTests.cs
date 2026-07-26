using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class WindowsShellSessionToolTests
{
    [Theory]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("restore")]
    public async Task Window_state_revalidates_the_approved_window(string state)
    {
        var windows = new FakeWindowService();
        var host = Host(new AppSetWindowStateTool(windows));
        var invocation = Invocation("app.set_window_state.v1", "R1", new Dictionary<string, object?>
        {
            ["windowId"] = "0x123",
            ["state"] = state,
            ["reason"] = "창 정리",
        });

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(result.Success, result.Error);
        Assert.Equal(state, Assert.Single(windows.States));
    }

    [Fact]
    public async Task Settings_tool_opens_only_a_known_page_id()
    {
        var launcher = new FakeSettingsLauncher();
        var host = Host(new SystemOpenSettingsTool(launcher));
        var valid = Invocation("system.open_settings.v1", "R1", new Dictionary<string, object?> { ["page"] = "windows_update", ["reason"] = "업데이트 확인" });
        var invalid = Invocation("system.open_settings.v1", "R1", new Dictionary<string, object?> { ["page"] = "../../shell", ["reason"] = "잘못된 URI" });

        Assert.True(host.CreateApprovalPrompt(valid).Success);
        Assert.True((await host.ExecuteAsync(valid)).Success);
        Assert.Equal("windows_update", Assert.Single(launcher.Pages));
        Assert.False(host.CreateApprovalPrompt(invalid).Success);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("sleep")]
    public async Task Session_actions_are_R2_and_require_an_exact_action(string action)
    {
        var sessions = new FakeSessionActions();
        var host = Host(new SystemSessionActionTool(sessions));
        var invocation = Invocation("system.session_action.v1", "R2", new Dictionary<string, object?> { ["action"] = action, ["reason"] = "사용자 요청" });

        var preview = host.CreateApprovalPrompt(invocation);
        Assert.True(preview.Success);
        Assert.Equal("R2", preview.Prompt!.Risk);
        Assert.True((await host.ExecuteAsync(invocation)).Success);
        Assert.Equal(action, Assert.Single(sessions.Actions));
    }

    private static WindowsToolHost Host(IWindowsToolAdapter adapter) => new(WindowsPlatformProfile.Classify(10, 0, 26_100, true), [adapter]);
    private static ToolInvokeParams Invocation(string name, string risk, IReadOnlyDictionary<string, object?> input) => new(Guid.NewGuid().ToString("N"), "conversation", "run", name, risk, input);

    private sealed class FakeWindowService : IWindowActionService
    {
        public List<string> States { get; } = [];
        public Task<WindowActionTarget?> ResolveAsync(string windowId, CancellationToken cancellationToken) => Task.FromResult<WindowActionTarget?>(new(windowId, "보고서", "notepad"));
        public Task<bool> ActivateAsync(WindowActionTarget target, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> CloseAsync(WindowActionTarget target, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> SetStateAsync(WindowActionTarget target, string state, CancellationToken cancellationToken) { States.Add(state); return Task.FromResult(true); }
    }
    private sealed class FakeSettingsLauncher : IWindowsSettingsLauncher { public List<string> Pages { get; } = []; public bool Open(string page) { Pages.Add(page); return true; } }
    private sealed class FakeSessionActions : IWindowsSessionActionService { public List<string> Actions { get; } = []; public bool Request(string action) { Actions.Add(action); return true; } }
}
