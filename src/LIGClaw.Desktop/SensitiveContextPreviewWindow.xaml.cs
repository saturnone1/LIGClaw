using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

public partial class SensitiveContextPreviewWindow : Window
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private PreparedSensitiveContext? _context;
    private byte[]? _approvedOcrTextUtf8;
    private bool _imageReady;
    private bool _cleanedUp;

    internal SensitiveContextPreviewWindow(PreparedSensitiveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        InitializeComponent();
        TargetSummaryText.Text = $"{TargetLabel(context.TargetKind)} · {context.WidthPixels:N0} × {context.HeightPixels:N0}px · 2분 뒤 자동 폐기";
        LoadPreview(context);
        Loaded += (_, _) => CancelButton.Focus();
        UpdateValidation();
    }

    internal int RedactionCount { get; private set; }

    ~SensitiveContextPreviewWindow()
    {
        ClearUnclaimedApproval();
    }

    internal byte[]? TakeApprovedOcrTextUtf8()
    {
        var value = _approvedOcrTextUtf8;
        _approvedOcrTextUtf8 = null;
        if (_cleanedUp) GC.SuppressFinalize(this);
        return value;
    }

    internal byte[]? CreateApprovedText() =>
        SensitiveContextPreviewPolicy.CreateApprovedText(OcrTextBox.Text, _imageReady).OcrTextUtf8;

    protected override void OnClosed(EventArgs e)
    {
        Cleanup();
        base.OnClosed(e);
    }

    private void LoadPreview(PreparedSensitiveContext context)
    {
        byte[]? imageCopy = null;
        try
        {
            imageCopy = context.EncodedImage.ToArray();
            using var stream = new MemoryStream(imageCopy, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
            _imageReady = bitmap.PixelWidth is >= 1 and <= PreparedSensitiveContextStore.MaximumDimensionPixels &&
                          bitmap.PixelHeight is >= 1 and <= PreparedSensitiveContextStore.MaximumDimensionPixels &&
                          (long)bitmap.PixelWidth * bitmap.PixelHeight <= PreparedSensitiveContextStore.MaximumPixelCount;
            if (!_imageReady) PreviewImage.Source = null;
        }
        catch (Exception exception) when (exception is
            NotSupportedException or InvalidOperationException or IOException or ArgumentException)
        {
            _imageReady = false;
            PreviewImage.Source = null;
        }
        finally
        {
            if (imageCopy is { Length: > 0 }) CryptographicOperations.ZeroMemory(imageCopy);
        }

        try
        {
            OcrTextBox.Text = StrictUtf8.GetString(context.OcrTextUtf8.Span);
        }
        catch (DecoderFallbackException)
        {
            OcrTextBox.Text = string.Empty;
        }
    }

    private void OcrTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateValidation();

    private void MaskSelection_Click(object sender, RoutedEventArgs e)
    {
        if (OcrTextBox.SelectionLength <= 0)
        {
            PreviewStatusText.Text = "먼저 가릴 글자를 선택하세요.";
            OcrTextBox.Focus();
            return;
        }
        var masked = SensitiveContextPreviewPolicy.MaskSelection(
            OcrTextBox.Text, OcrTextBox.SelectionStart, OcrTextBox.SelectionLength);
        if (masked is null) return;
        var caret = Math.Min(OcrTextBox.SelectionStart + "[가림]".Length, masked.Length);
        OcrTextBox.Text = masked;
        OcrTextBox.CaretIndex = caret;
        RedactionCount++;
        PreviewStatusText.Text = "선택한 글자를 마스킹했습니다.";
        OcrTextBox.Focus();
    }

    private void ClearText_Click(object sender, RoutedEventArgs e)
    {
        if (OcrTextBox.Text.Length > 0) RedactionCount++;
        OcrTextBox.Clear();
        PreviewStatusText.Text = "인식된 글자를 모두 지웠습니다.";
        OcrTextBox.Focus();
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        _approvedOcrTextUtf8 = CreateApprovedText();
        if (_approvedOcrTextUtf8 is null)
        {
            UpdateValidation();
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateValidation()
    {
        var validation = SensitiveContextPreviewPolicy.Validate(OcrTextBox.Text, _imageReady);
        ApproveButton.IsEnabled = validation.Success;
        ValidationText.Text = validation.Error ?? string.Empty;
        if (!_imageReady)
            PreviewStatusText.Text = "이미지를 안전하게 표시하지 못해 전송할 수 없습니다.";
        else if (string.IsNullOrEmpty(PreviewStatusText.Text))
            PreviewStatusText.Text = "확인한 OCR 글자만 전송하며 이미지는 전송하지 않습니다.";
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        PreviewImage.Source = null;
        OcrTextBox.Clear();
        _context?.Dispose();
        _context = null;
        if (_approvedOcrTextUtf8 is null) GC.SuppressFinalize(this);
    }

    private void ClearUnclaimedApproval()
    {
        if (_approvedOcrTextUtf8 is not { Length: > 0 } value) return;
        CryptographicOperations.ZeroMemory(value);
        _approvedOcrTextUtf8 = null;
    }

    private static string TargetLabel(string targetKind) => targetKind switch
    {
        "window" => "선택한 창",
        "display" => "선택한 디스플레이",
        "region" => "선택한 영역",
        _ => "선택한 화면",
    };
}
