using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum ScreenTextRecognitionStatus
{
    Recognized,
    PackageIdentityRequired,
    LanguageUnavailable,
    NoText,
    Failed,
}

internal sealed record ScreenTextRecognitionResult(
    ScreenTextRecognitionStatus Status,
    byte[]? TextUtf8 = null,
    bool WasTruncated = false,
    string? Error = null)
{
    public void ClearText()
    {
        if (TextUtf8 is { Length: > 0 }) CryptographicOperations.ZeroMemory(TextUtf8);
    }
}

internal interface IScreenTextRecognizer
{
    Task<ScreenTextRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> encodedImage,
        CancellationToken cancellationToken = default);
}

internal static class OcrBitmapPolicy
{
    internal static (uint Width, uint Height) ScaleToMaximum(uint width, uint height, uint maximumDimension)
    {
        if (width == 0 || height == 0 || maximumDimension == 0) return (0, 0);
        var largest = Math.Max(width, height);
        if (largest <= maximumDimension) return (width, height);
        var scale = maximumDimension / (double)largest;
        return (
            Math.Max(1, (uint)Math.Round(width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (uint)Math.Round(height * scale, MidpointRounding.AwayFromZero)));
    }
}

internal sealed class WindowsOcrScreenTextRecognizer : IScreenTextRecognizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ScreenTextRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> encodedImage,
        CancellationToken cancellationToken = default)
    {
        if (encodedImage.IsEmpty || encodedImage.Length > PreparedSensitiveContextStore.MaximumEncodedImageBytes)
            return new(ScreenTextRecognitionStatus.Failed, Error: "화면 이미지 형식이 올바르지 않아요.");

        if (!WindowsPackageIdentity.IsAvailable())
            return new(
                ScreenTextRecognitionStatus.PackageIdentityRequired,
                Error: "설치된 LIGClaw에서만 Windows 글자 인식을 사용할 수 있어요.");

        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
                return new(
                    ScreenTextRecognitionStatus.LanguageUnavailable,
                    Error: "Windows에 현재 언어의 OCR 기능을 추가한 뒤 다시 시도해 주세요.");

            using var stream = new InMemoryRandomAccessStream();
            byte[]? imageCopy = null;
            using (var writer = new DataWriter(stream))
            {
                try
                {
                    imageCopy = encodedImage.ToArray();
                    writer.WriteBytes(imageCopy);
                    _ = await writer.StoreAsync();
                    _ = writer.DetachStream();
                }
                finally
                {
                    if (imageCopy is { Length: > 0 }) CryptographicOperations.ZeroMemory(imageCopy);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scaled = OcrBitmapPolicy.ScaleToMaximum(
                decoder.PixelWidth,
                decoder.PixelHeight,
                checked((uint)OcrEngine.MaxImageDimension));
            if (scaled.Width == 0 || scaled.Height == 0)
                return new(ScreenTextRecognitionStatus.Failed, Error: "화면 이미지 크기가 올바르지 않아요.");
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = scaled.Width, ScaledHeight = scaled.Height },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            var result = await engine.RecognizeAsync(bitmap);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Text))
                return new(ScreenTextRecognitionStatus.NoText, Error: "선택한 화면에서 읽을 수 있는 글자를 찾지 못했어요.");

            var (text, wasTruncated) = EncodeBounded(result.Text);
            return new(ScreenTextRecognitionStatus.Recognized, text, wasTruncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            return new(ScreenTextRecognitionStatus.Failed, Error: "Windows가 화면의 글자를 읽지 못했어요.");
        }
    }

    internal static (byte[] TextUtf8, bool WasTruncated) EncodeBounded(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (StrictUtf8.GetByteCount(text) <= PreparedSensitiveContextStore.MaximumOcrTextBytes)
            return (StrictUtf8.GetBytes(text), false);

        var buffer = GC.AllocateUninitializedArray<byte>(PreparedSensitiveContextStore.MaximumOcrTextBytes);
        try
        {
            StrictUtf8.GetEncoder().Convert(
                text.AsSpan(),
                buffer,
                true,
                out _,
                out var bytesUsed,
                out _);
            return (buffer[..bytesUsed], true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

}
