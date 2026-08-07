using System.Text;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ScreenContextPolicyTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(4_096, 3_906, true)]
    [InlineData(4_097, 1, false)]
    [InlineData(1, 4_097, false)]
    [InlineData(4_000, 4_001, false)]
    [InlineData(0, 1, false)]
    public void Capture_dimensions_are_bounded_before_frame_allocation(
        int width,
        int height,
        bool valid)
    {
        var error = ScreenCapturePolicy.ValidateDimensions(width, height);

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Ocr_text_is_truncated_on_a_unicode_boundary_and_within_the_byte_limit()
    {
        var input = string.Concat(Enumerable.Repeat("화면😀", 12_000));

        var result = WindowsOcrScreenTextRecognizer.EncodeBounded(input);
        var decoded = new UTF8Encoding(false, true).GetString(result.TextUtf8);

        Assert.True(result.WasTruncated);
        Assert.InRange(result.TextUtf8.Length, 1, PreparedSensitiveContextStore.MaximumOcrTextBytes);
        Assert.StartsWith(decoded, input, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', decoded);
    }

    [Fact]
    public void Ocr_text_within_the_limit_is_preserved()
    {
        const string input = "일반 문서 화면";

        var result = WindowsOcrScreenTextRecognizer.EncodeBounded(input);

        Assert.False(result.WasTruncated);
        Assert.Equal(input, Encoding.UTF8.GetString(result.TextUtf8));
    }

    [Theory]
    [InlineData(3840, 2160, 2600, 2600, 1463)]
    [InlineData(1080, 1920, 2600, 1080, 1920)]
    [InlineData(1, 4096, 2600, 1, 2600)]
    public void Ocr_input_is_downscaled_without_rejecting_common_high_resolution_screens(
        uint width,
        uint height,
        uint maximum,
        uint expectedWidth,
        uint expectedHeight)
    {
        var scaled = OcrBitmapPolicy.ScaleToMaximum(width, height, maximum);

        Assert.Equal((expectedWidth, expectedHeight), scaled);
    }

    [Fact]
    public void Uniform_crop_maps_display_coordinates_to_source_pixels()
    {
        var result = SensitiveImageCropPolicy.MapUniformSelection(
            100,
            50,
            1000,
            500,
            10,
            10,
            90,
            40);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new PixelCropRectangle(100, 100, 800, 300), result.Rectangle);
    }

    [Fact]
    public void Uniform_crop_excludes_letterbox_space_and_clamps_reverse_drag()
    {
        var result = SensitiveImageCropPolicy.MapUniformSelection(
            100,
            100,
            200,
            100,
            120,
            110,
            -20,
            -10);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new PixelCropRectangle(0, 0, 200, 100), result.Rectangle);
    }

    [Theory]
    [InlineData(100, 100, 1000, 1000, 10, 10, 10.5, 10.5)]
    [InlineData(0, 100, 1000, 1000, 10, 10, 90, 90)]
    [InlineData(100, 100, 0, 1000, 10, 10, 90, 90)]
    public void Invalid_or_tiny_crop_is_rejected(
        double displayWidth,
        double displayHeight,
        int sourceWidth,
        int sourceHeight,
        double startX,
        double startY,
        double endX,
        double endY)
    {
        var result = SensitiveImageCropPolicy.MapUniformSelection(
            displayWidth,
            displayHeight,
            sourceWidth,
            sourceHeight,
            startX,
            startY,
            endX,
            endY);

        Assert.False(result.Success);
        Assert.Null(result.Rectangle);
    }
}
