using System.Diagnostics;
using System.Runtime.InteropServices;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class UiAutomationWindowsIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task InspectsAndResolvesTheSameWpfElementsAfterWindowMovement()
    {
        using var fixture = StartFixture();
        try
        {
            var window = await WaitForWindowAsync(fixture);
            var windowId = $"0x{window.ToInt64():X}";
            var automation = new WindowsUiAutomationService();

            var inspection = Assert.IsType<UiAutomationInspection>(
                await automation.InspectAsync(windowId, "Fixture", 20, CancellationToken.None));
            var input = Assert.Single(inspection.Elements, element => element.AutomationId == "FixtureInput");
            var password = Assert.Single(inspection.Elements, element => element.AutomationId == "FixturePassword");
            var save = Assert.Single(inspection.Elements, element => element.AutomationId == "SaveButton");

            Assert.True(input.SupportsValue);
            Assert.False(password.SupportsValue);
            Assert.True(save.SupportsInvoke);
            Assert.True(SetWindowPos(window, IntPtr.Zero, 80, 80, 440, 280, 0x0010));

            var inputTarget = Assert.IsType<UiAutomationElementTarget>(
                await automation.ResolveAsync(input.ElementId, CancellationToken.None));
            var saveTarget = Assert.IsType<UiAutomationElementTarget>(
                await automation.ResolveAsync(save.ElementId, CancellationToken.None));
            Assert.Equal("FixtureInput", inputTarget.AutomationId);
            Assert.Equal("SaveButton", saveTarget.AutomationId);
            Assert.Equal(windowId, inputTarget.WindowId);
        }
        finally
        {
            if (!fixture.HasExited)
            {
                _ = fixture.CloseMainWindow();
                if (!fixture.WaitForExit(3_000)) fixture.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task InspectsAndResolvesNativeWin32ControlsAfterWindowMovement()
    {
        using var fixture = StartFixture("LIGClaw.Win32AutomationFixture");
        try
        {
            var window = await WaitForWindowAsync(fixture);
            var windowId = $"0x{window.ToInt64():X}";
            var automation = new WindowsUiAutomationService();

            var inspection = Assert.IsType<UiAutomationInspection>(
                await automation.InspectAsync(windowId, "Win32", 20, CancellationToken.None));
            var save = Assert.Single(inspection.Elements, element => element.Name.Contains("Save Win32", StringComparison.Ordinal));

            Assert.True(save.SupportsInvoke);
            Assert.True(SetWindowPos(window, IntPtr.Zero, 140, 120, 440, 250, 0x0010));
            var resolved = Assert.IsType<UiAutomationElementTarget>(
                await automation.ResolveAsync(save.ElementId, CancellationToken.None));
            Assert.Equal("Button", resolved.ControlType);
            Assert.Equal(windowId, resolved.WindowId);
        }
        finally
        {
            if (!fixture.HasExited)
            {
                _ = fixture.CloseMainWindow();
                if (!fixture.WaitForExit(3_000)) fixture.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartFixture(string projectName = "LIGClaw.UiAutomationFixture")
    {
        var root = FindRepositoryRoot();
        var executable = Path.Combine(
            root,
            "tests",
            projectName,
            "bin",
            "Debug",
            "net10.0-windows10.0.17763.0",
            $"{projectName}.exe");
        return Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })
               ?? throw new InvalidOperationException("UI Automation fixture를 시작하지 못했습니다.");
    }

    private static async Task<IntPtr> WaitForWindowAsync(Process process)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("UI Automation fixture가 조기에 종료됐습니다.");
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            await Task.Delay(100);
        }
        throw new TimeoutException("UI Automation fixture 창을 찾지 못했습니다.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LIGClaw.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("LIGClaw repository root를 찾지 못했습니다.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
