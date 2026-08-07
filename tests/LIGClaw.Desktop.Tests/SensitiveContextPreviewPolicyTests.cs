using System.Security.Cryptography;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class SensitiveContextPreviewPolicyTests
{
    [Fact]
    public void Selected_text_is_replaced_without_retaining_the_original_selection()
    {
        var masked = SensitiveContextPreviewPolicy.MaskSelection("비밀 공개 문장", 0, 2);

        Assert.Equal("[가림] 공개 문장", masked);
        Assert.DoesNotContain("비밀", masked, StringComparison.Ordinal);
        Assert.Null(SensitiveContextPreviewPolicy.MaskSelection("글자", 1, 0));
        Assert.Null(SensitiveContextPreviewPolicy.MaskSelection("글자", 2, 1));
    }

    [Fact]
    public void Only_nonempty_bounded_text_with_a_visible_image_can_be_approved()
    {
        var hiddenImage = SensitiveContextPreviewPolicy.CreateApprovedText("공개", imageReady: false);
        var empty = SensitiveContextPreviewPolicy.CreateApprovedText("  ", imageReady: true);
        var oversized = SensitiveContextPreviewPolicy.CreateApprovedText(new string('가', 11_000), imageReady: true);
        var valid = SensitiveContextPreviewPolicy.CreateApprovedText("확인한 글자", imageReady: true);

        Assert.False(hiddenImage.Success);
        Assert.Contains("미리보기", hiddenImage.Error, StringComparison.Ordinal);
        Assert.False(empty.Success);
        Assert.False(oversized.Success);
        Assert.Contains("32KiB", oversized.Error, StringComparison.Ordinal);
        Assert.True(valid.Success, valid.Error);
        var bytes = Assert.IsType<byte[]>(valid.OcrTextUtf8);
        try
        {
            Assert.Equal("확인한 글자", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
