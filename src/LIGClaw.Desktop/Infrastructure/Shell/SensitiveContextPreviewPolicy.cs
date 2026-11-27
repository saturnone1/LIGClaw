using System.Text;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record SensitiveContextPreviewTextResult(
    bool Success,
    byte[]? OcrTextUtf8 = null,
    string? Error = null);

internal static class SensitiveContextPreviewPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SensitiveContextPreviewTextResult Validate(string text, bool imageReady)
    {
        if (!imageReady)
            return new SensitiveContextPreviewTextResult(false, Error: "화면 미리보기를 확인할 수 없습니다.");
        if (string.IsNullOrWhiteSpace(text))
            return new SensitiveContextPreviewTextResult(false, Error: "전송할 글자가 없습니다.");
        if (StrictUtf8.GetByteCount(text) > PreparedSensitiveContextStore.MaximumOcrTextBytes)
            return new SensitiveContextPreviewTextResult(
                false, Error: "전송할 글자가 32KiB 한도를 넘었습니다. 필요 없는 내용을 줄여 주세요.");
        return new SensitiveContextPreviewTextResult(true);
    }

    public static SensitiveContextPreviewTextResult CreateApprovedText(string text, bool imageReady)
    {
        var validation = Validate(text, imageReady);
        return validation.Success
            ? new SensitiveContextPreviewTextResult(true, StrictUtf8.GetBytes(text))
            : validation;
    }

    public static string? MaskSelection(string text, int selectionStart, int selectionLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (selectionStart < 0 || selectionLength <= 0 || selectionStart > text.Length - selectionLength)
            return null;
        return string.Concat(text.AsSpan(0, selectionStart), "[가림]", text.AsSpan(selectionStart + selectionLength));
    }
}
