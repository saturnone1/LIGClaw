using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LIGClaw.Desktop.Infrastructure.Shell;
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
            SensitiveContextPreviewWindow? sensitiveWindow = null;
            SensitiveContextPreviewWindow? invalidSensitiveWindow = null;
            PreparedSensitiveContextStore? sensitiveStore = null;
            PreparedSensitiveContext? sensitiveContext = null;
            PreparedSensitiveContext? invalidSensitiveContext = null;
            byte[]? approvedOcr = null;
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

                sensitiveStore = new PreparedSensitiveContextStore();
                var identity = new PreparedSensitiveContextIdentity("conversation", "run", "tool-call");
                var image = CreatePng(16, 16);
                var ocr = Encoding.UTF8.GetBytes("비밀 공개 문장");
                var prepared = sensitiveStore.Prepare(identity, new PreparedSensitiveContextDraft(
                    "window", 16, 16, image, ocr, 0));
                Assert.True(prepared.Success, prepared.Error);
                sensitiveContext = Assert.IsType<PreparedSensitiveContext>(
                    sensitiveStore.Take(identity, prepared.Token!).Context);
                sensitiveWindow = new SensitiveContextPreviewWindow(
                    sensitiveContext,
                    new StubScreenTextRecognizer("비밀 공개 문장"));
                sensitiveContext = null;
                var sensitiveContent = Assert.IsAssignableFrom<FrameworkElement>(sensitiveWindow.Content);
                sensitiveContent.Measure(new Size(920, 700));
                sensitiveContent.Arrange(new Rect(0, 0, 920, 700));
                sensitiveContent.UpdateLayout();
                Assert.NotNull(sensitiveWindow.PreviewImage.Source);
                Assert.True(sensitiveWindow.ApproveButton.IsEnabled);
                Assert.True(sensitiveWindow.ApplyCropAsync(new PixelCropRectangle(0, 0, 8, 8)).GetAwaiter().GetResult());
                var croppedSource = Assert.IsAssignableFrom<BitmapSource>(sensitiveWindow.PreviewImage.Source);
                Assert.Equal(8, croppedSource.PixelWidth);
                Assert.Equal(8, croppedSource.PixelHeight);
                sensitiveWindow.OcrTextBox.Select(0, 2);
                sensitiveWindow.MaskSelectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("[가림] 공개 문장", sensitiveWindow.OcrTextBox.Text);
                approvedOcr = sensitiveWindow.CreateApprovedText();
                Assert.Equal("[가림] 공개 문장", Encoding.UTF8.GetString(Assert.IsType<byte[]>(approvedOcr)));
                sensitiveWindow.Close();
                sensitiveWindow = null;
                Assert.True(image.All(value => value == 0));
                Assert.True(ocr.All(value => value == 0));

                var invalidPrepared = sensitiveStore.Prepare(identity with { ToolCallId = "invalid" },
                    new PreparedSensitiveContextDraft("window", 1, 1, [1, 2, 3], Encoding.UTF8.GetBytes("글자"), 0));
                Assert.True(invalidPrepared.Success, invalidPrepared.Error);
                invalidSensitiveContext = Assert.IsType<PreparedSensitiveContext>(sensitiveStore.Take(
                    identity with { ToolCallId = "invalid" }, invalidPrepared.Token!).Context);
                invalidSensitiveWindow = new SensitiveContextPreviewWindow(invalidSensitiveContext);
                invalidSensitiveContext = null;
                Assert.Null(invalidSensitiveWindow.PreviewImage.Source);
                Assert.False(invalidSensitiveWindow.ApproveButton.IsEnabled);
                Assert.Contains("미리보기", invalidSensitiveWindow.ValidationText.Text, StringComparison.Ordinal);

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
                sensitiveContext?.Dispose();
                invalidSensitiveContext?.Dispose();
                sensitiveWindow?.Close();
                invalidSensitiveWindow?.Close();
                sensitiveStore?.Dispose();
                if (approvedOcr is { Length: > 0 }) CryptographicOperations.ZeroMemory(approvedOcr);
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

    private sealed class StubScreenTextRecognizer(string text) : IScreenTextRecognizer
    {
        public Task<ScreenTextRecognitionResult> RecognizeAsync(
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(encodedImage.IsEmpty);
            return Task.FromResult(new ScreenTextRecognitionResult(
                ScreenTextRecognitionStatus.Recognized,
                Encoding.UTF8.GetBytes(text)));
        }
    }

    private static byte[] CreatePng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x6D;
            pixels[index + 1] = 0x2F;
            pixels[index + 2] = 0x00;
            pixels[index + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
