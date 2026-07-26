using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class UiConstructionTests
{
    [Fact]
    public void CriticalUiConstructsAndLaysOutOnAnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? mainWindow = null;
            ToolApprovalWindow? window = null;
            DiagnosticsWindow? diagnosticsWindow = null;
            try
            {
                app = new App();
                app.InitializeComponent();
                mainWindow = new MainWindow(new NoOpNotificationService());
                var mainContent = Assert.IsAssignableFrom<FrameworkElement>(mainWindow.Content);
                mainContent.Measure(new Size(1060, 650));
                mainContent.Arrange(new Rect(0, 0, 1060, 650));
                mainContent.UpdateLayout();
                Assert.True(mainContent.DesiredSize.Width > 0);
                window = new ToolApprovalWindow(new WindowsToolApprovalPrompt(
                    "파일을 이동할까요?",
                    "보고서 파일을 보관 폴더로 이동합니다.",
                    "대상 C:\\Work\\report.txt → C:\\Archive\\report.txt",
                    "R2",
                    CanUndo: true,
                    GrantScope: "fixture:scope"),
                    allowAlways: true);

                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(500, 640));
                content.Arrange(new Rect(0, 0, 500, Math.Max(300, content.DesiredSize.Height)));
                content.UpdateLayout();

                Assert.Equal("동작 승인", window.Title);
                Assert.True(content.DesiredSize.Width > 0);
                Assert.True(content.DesiredSize.Height > 0);
                Assert.Equal(Visibility.Visible, window.AllowAlwaysButton.Visibility);

                diagnosticsWindow = new DiagnosticsWindow(new ObservableCollection<string> { "12:00:00 test" });
                var diagnosticsContent = Assert.IsAssignableFrom<FrameworkElement>(diagnosticsWindow.Content);
                diagnosticsContent.Measure(new Size(820, 560));
                diagnosticsContent.Arrange(new Rect(0, 0, 820, 560));
                diagnosticsContent.UpdateLayout();
                Assert.Equal("문제 해결 정보", diagnosticsWindow.Title);
                Assert.True(diagnosticsContent.DesiredSize.Height > 0);

                VerifyLargeActivityList();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                mainWindow?.Close();
                window?.Close();
                diagnosticsWindow?.Close();
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "승인 창 구성 테스트가 제한 시간 안에 끝나지 않았습니다.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NoOpNotificationService : IUserNotificationService
    {
        public Task ShowAsync(string title, string message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void VerifyLargeActivityList()
    {
        var longSummary = string.Concat(Enumerable.Repeat(
            "아주 긴 한국어 실행 설명이 좁은 화면에서도 잘리지 않고 여러 줄로 표시되어야 합니다. ", 12));
        var activity = Enumerable.Range(0, 1_000)
            .Select(index => new ToolActivitySummary(
                index,
                "system.get_storage_status.v1",
                "R0",
                index % 7 == 0 ? "failed" : "succeeded",
                $"{index}: {longSummary}",
                true,
                DateTimeOffset.UtcNow.AddMinutes(-index)))
            .ToArray();
        var view = new ActivityView(activity, []);

        view.Measure(new Size(588, 500));
        view.Arrange(new Rect(0, 0, 588, 500));
        view.UpdateLayout();

        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.ActivityList));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(view.ActivityList));
        Assert.Equal(1_000, view.ActivityList.Items.Count);
        Assert.InRange(view.DesiredSize.Width, 1, 588);
        Assert.InRange(view.DesiredSize.Height, 1, 500);
        Assert.Null(view.ActivityList.ItemContainerGenerator.ContainerFromIndex(999));
    }
}
