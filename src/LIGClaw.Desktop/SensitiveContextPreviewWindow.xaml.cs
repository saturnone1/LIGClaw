using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LIGClaw.Desktop.Infrastructure.Shell;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace LIGClaw.Desktop;

public partial class SensitiveContextPreviewWindow : Window
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IScreenTextRecognizer? _screenTextRecognizer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private PreparedSensitiveContext? _context;
    private BitmapSource? _originalBitmap;
    private BitmapSource? _currentBitmap;
    private Point? _selectionStart;
    private PixelCropRectangle? _pendingCrop;
    private byte[]? _approvedOcrTextUtf8;
    private bool _imageReady;
    private bool _isCropping;
    private bool _cleanedUp;

    internal SensitiveContextPreviewWindow(PreparedSensitiveContext context)
        : this(context, null)
    {
    }

    internal SensitiveContextPreviewWindow(
        PreparedSensitiveContext context,
        IScreenTextRecognizer? screenTextRecognizer)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _screenTextRecognizer = screenTextRecognizer;
        _context.Expired += Context_Expired;
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
        _lifetimeCancellation.Cancel();
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
            _originalBitmap = bitmap;
            _currentBitmap = bitmap;
            PreviewImage.Source = _currentBitmap;
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

    private void Context_Expired(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_cleanedUp) return;
            PreviewStatusText.Text = "화면 내용이 자동 폐기됐어요. 다시 가져와 주세요.";
            Close();
        });
    }

    private void CropOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_imageReady || _isCropping || _currentBitmap is null) return;
        _selectionStart = ClampToOverlay(e.GetPosition(CropOverlay));
        _pendingCrop = null;
        CropSelectionButton.IsEnabled = false;
        CropSelectionRectangle.Visibility = Visibility.Visible;
        UpdateSelectionRectangle(_selectionStart.Value, _selectionStart.Value);
        _ = CropOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void CropOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_selectionStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateSelectionRectangle(start, ClampToOverlay(e.GetPosition(CropOverlay)));
    }

    private void CropOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionStart is not { } start || _currentBitmap is null) return;
        var end = ClampToOverlay(e.GetPosition(CropOverlay));
        CropOverlay.ReleaseMouseCapture();
        _selectionStart = null;
        UpdateSelectionRectangle(start, end);
        var selection = SensitiveImageCropPolicy.MapUniformSelection(
            CropOverlay.ActualWidth,
            CropOverlay.ActualHeight,
            _currentBitmap.PixelWidth,
            _currentBitmap.PixelHeight,
            start.X,
            start.Y,
            end.X,
            end.Y);
        if (!selection.Success || selection.Rectangle is null)
        {
            ClearCropSelection();
            PreviewStatusText.Text = selection.Error ?? "이미지 영역을 다시 선택해 주세요.";
            return;
        }

        _pendingCrop = selection.Rectangle;
        CropSelectionButton.IsEnabled = _screenTextRecognizer is not null;
        PreviewStatusText.Text = _screenTextRecognizer is null
            ? "이 실행에서는 영역의 글자를 다시 읽을 수 없어요."
            : $"{selection.Rectangle.Width:N0} × {selection.Rectangle.Height:N0}px 영역을 선택했습니다.";
        e.Handled = true;
    }

    private async void CropSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_isCropping || _pendingCrop is not { } crop || _currentBitmap is null || _screenTextRecognizer is null)
            return;

        _ = await ApplyCropAsync(crop);
    }

    internal async Task<bool> ApplyCropAsync(PixelCropRectangle crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        if (_isCropping || _currentBitmap is null || _screenTextRecognizer is null) return false;

        _isCropping = true;
        byte[]? croppedImage = null;
        ScreenTextRecognitionResult? recognition = null;
        UpdateValidation();
        CropSelectionButton.IsEnabled = false;
        RestoreImageButton.IsEnabled = false;
        PreviewStatusText.Text = "선택 영역에서 글자를 다시 읽고 있어요…";
        try
        {
            croppedImage = EncodeCrop(_currentBitmap, crop);
            if (croppedImage is null)
            {
                PreviewStatusText.Text = "선택 영역 이미지를 안전하게 만들지 못했어요.";
                return false;
            }

            recognition = await _screenTextRecognizer.RecognizeAsync(
                croppedImage,
                _lifetimeCancellation.Token);
            if (recognition.Status != ScreenTextRecognitionStatus.Recognized || recognition.TextUtf8 is null)
            {
                PreviewStatusText.Text = recognition.Error ?? "선택 영역에서 글자를 읽지 못했어요.";
                return false;
            }

            var croppedBitmap = DecodeBitmap(croppedImage);
            var recognizedText = StrictUtf8.GetString(recognition.TextUtf8);
            _currentBitmap = croppedBitmap;
            PreviewImage.Source = _currentBitmap;
            OcrTextBox.Text = recognizedText;
            RedactionCount++;
            TargetSummaryText.Text = $"선택 영역 · {_currentBitmap.PixelWidth:N0} × {_currentBitmap.PixelHeight:N0}px · 준비 후 2분 뒤 자동 폐기";
            ClearCropSelection();
            RestoreImageButton.IsEnabled = true;
            PreviewStatusText.Text = recognition.WasTruncated
                ? "선택 영역의 글자가 길어 안전 한도까지만 다시 읽었습니다."
                : "선택 영역과 새로 읽은 글자로 바꿨습니다.";
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or ArgumentException or IOException or DecoderFallbackException)
        {
            PreviewStatusText.Text = "선택 영역을 자르지 못했어요. 영역을 다시 선택해 주세요.";
            return false;
        }
        finally
        {
            if (croppedImage is { Length: > 0 }) CryptographicOperations.ZeroMemory(croppedImage);
            recognition?.ClearText();
            _isCropping = false;
            if (!_cleanedUp)
            {
                RestoreImageButton.IsEnabled = !ReferenceEquals(_currentBitmap, _originalBitmap);
                CropSelectionButton.IsEnabled = _pendingCrop is not null && _screenTextRecognizer is not null;
                UpdateValidation();
            }
        }
    }

    private void RestoreImage_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null || _context is null || _isCropping) return;
        _currentBitmap = _originalBitmap;
        PreviewImage.Source = _currentBitmap;
        try
        {
            OcrTextBox.Text = StrictUtf8.GetString(_context.OcrTextUtf8.Span);
        }
        catch (DecoderFallbackException)
        {
            OcrTextBox.Clear();
        }

        TargetSummaryText.Text = $"{TargetLabel(_context.TargetKind)} · {_context.WidthPixels:N0} × {_context.HeightPixels:N0}px · 2분 뒤 자동 폐기";
        ClearCropSelection();
        RestoreImageButton.IsEnabled = false;
        PreviewStatusText.Text = "전체 화면과 처음 인식한 글자를 복원했습니다.";
    }

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
        ApproveButton.IsEnabled = validation.Success && !_isCropping;
        ValidationText.Text = validation.Error ?? string.Empty;
        if (!_imageReady)
            PreviewStatusText.Text = "이미지를 안전하게 표시하지 못해 요청에 추가할 수 없습니다.";
        else if (string.IsNullOrEmpty(PreviewStatusText.Text))
            PreviewStatusText.Text = "확인한 OCR 글자만 요청에 추가하며 이미지는 추가하지 않습니다.";
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        PreviewImage.Source = null;
        _originalBitmap = null;
        _currentBitmap = null;
        ClearCropSelection();
        OcrTextBox.Clear();
        if (_context is not null)
        {
            _context.Expired -= Context_Expired;
            _context.Dispose();
        }
        _context = null;
        _lifetimeCancellation.Dispose();
        if (_approvedOcrTextUtf8 is null) GC.SuppressFinalize(this);
    }

    private void ClearUnclaimedApproval()
    {
        if (_approvedOcrTextUtf8 is not { Length: > 0 } value) return;
        CryptographicOperations.ZeroMemory(value);
        _approvedOcrTextUtf8 = null;
    }

    private Point ClampToOverlay(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, CropOverlay.ActualWidth)),
        Math.Clamp(point.Y, 0, Math.Max(0, CropOverlay.ActualHeight)));

    private void UpdateSelectionRectangle(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        System.Windows.Controls.Canvas.SetLeft(CropSelectionRectangle, left);
        System.Windows.Controls.Canvas.SetTop(CropSelectionRectangle, top);
        CropSelectionRectangle.Width = Math.Abs(end.X - start.X);
        CropSelectionRectangle.Height = Math.Abs(end.Y - start.Y);
    }

    private void ClearCropSelection()
    {
        _selectionStart = null;
        _pendingCrop = null;
        if (CropOverlay.IsMouseCaptured) CropOverlay.ReleaseMouseCapture();
        CropSelectionRectangle.Visibility = Visibility.Collapsed;
        CropSelectionButton.IsEnabled = false;
    }

    private static byte[]? EncodeCrop(BitmapSource source, PixelCropRectangle crop)
    {
        if (crop.X < 0 || crop.Y < 0 || crop.Width < 1 || crop.Height < 1 ||
            crop.X + crop.Width > source.PixelWidth || crop.Y + crop.Height > source.PixelHeight)
            return null;

        var cropped = new CroppedBitmap(
            source,
            new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
        cropped.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var stream = new MemoryStream();
        try
        {
            encoder.Save(stream);
            if (stream.Length is < 1 or > PreparedSensitiveContextStore.MaximumEncodedImageBytes) return null;
            return stream.ToArray();
        }
        finally
        {
            if (stream.TryGetBuffer(out var buffer) && buffer.Count > 0)
                CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }
    }

    private static BitmapSource DecodeBitmap(byte[] encodedImage)
    {
        using var stream = new MemoryStream(encodedImage, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string TargetLabel(string targetKind) => targetKind switch
    {
        "window" => "선택한 창",
        "display" => "선택한 디스플레이",
        "region" => "선택한 영역",
        "selection" => "Windows에서 선택한 화면",
        _ => "선택한 화면",
    };
}
