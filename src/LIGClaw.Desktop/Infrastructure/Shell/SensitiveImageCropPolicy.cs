namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record PixelCropRectangle(int X, int Y, int Width, int Height);

internal sealed record SensitiveImageCropResult(
    bool Success,
    PixelCropRectangle? Rectangle = null,
    string? Error = null);

internal static class SensitiveImageCropPolicy
{
    private const int MinimumCropPixels = 8;

    internal static SensitiveImageCropResult MapUniformSelection(
        double displayWidth,
        double displayHeight,
        int sourceWidth,
        int sourceHeight,
        double startX,
        double startY,
        double endX,
        double endY)
    {
        if (!FinitePositive(displayWidth) || !FinitePositive(displayHeight) ||
            sourceWidth < 1 || sourceHeight < 1 ||
            !double.IsFinite(startX) || !double.IsFinite(startY) ||
            !double.IsFinite(endX) || !double.IsFinite(endY))
            return new(false, Error: "이미지 영역을 다시 선택해 주세요.");

        var scale = Math.Min(displayWidth / sourceWidth, displayHeight / sourceHeight);
        if (!FinitePositive(scale)) return new(false, Error: "이미지 영역을 다시 선택해 주세요.");
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var offsetX = (displayWidth - renderedWidth) / 2;
        var offsetY = (displayHeight - renderedHeight) / 2;

        var left = Math.Clamp(Math.Min(startX, endX), offsetX, offsetX + renderedWidth);
        var top = Math.Clamp(Math.Min(startY, endY), offsetY, offsetY + renderedHeight);
        var right = Math.Clamp(Math.Max(startX, endX), offsetX, offsetX + renderedWidth);
        var bottom = Math.Clamp(Math.Max(startY, endY), offsetY, offsetY + renderedHeight);

        var pixelLeft = Math.Clamp((int)Math.Floor((left - offsetX) / scale), 0, sourceWidth);
        var pixelTop = Math.Clamp((int)Math.Floor((top - offsetY) / scale), 0, sourceHeight);
        var pixelRight = Math.Clamp((int)Math.Ceiling((right - offsetX) / scale), 0, sourceWidth);
        var pixelBottom = Math.Clamp((int)Math.Ceiling((bottom - offsetY) / scale), 0, sourceHeight);
        var width = pixelRight - pixelLeft;
        var height = pixelBottom - pixelTop;
        if (width < MinimumCropPixels || height < MinimumCropPixels)
            return new(false, Error: "조금 더 넓은 영역을 선택해 주세요.");

        return new(true, new PixelCropRectangle(pixelLeft, pixelTop, width, height));
    }

    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
}
