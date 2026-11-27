using System.Collections;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Mail;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class MailCreateDraftTool : IWorkflowOnlyObservationToolAdapter
{
    private const int MaximumRecipientItems = MailDraftPolicy.MaximumRecipients;

    public string Name => "mail.create_draft.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.None;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadInput(input, out var draftInput))
            return Task.FromResult(Failure("mail.draft.input_invalid"));

        var validation = MailDraftPolicy.Validate(draftInput);
        if (!validation.IsValid || validation.Draft is null)
            return Task.FromResult(Failure(validation.ErrorCode ?? "mail.draft.input_invalid"));

        var draft = validation.Draft;
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["ready"] = true,
            ["draftHash"] = draft.CanonicalHash,
            ["recipientCount"] = (long)(draft.To.Count + draft.Cc.Count + draft.Bcc.Count),
            ["subjectLength"] = (long)CountRunes(draft.Subject),
            ["bodyLength"] = (long)CountRunes(draft.Body),
        };
        return Task.FromResult(new WindowsToolExecutionResult(
            true,
            output,
            ActivitySummary: "메일 초안 형식을 검증했어요."));
    }

    private static bool TryReadInput(
        IReadOnlyDictionary<string, object?> input,
        out MailDraftInput draft)
    {
        draft = null!;
        if (!ToolInputReader.HasOnlyKeys(input, "to", "cc", "bcc", "subject", "body") ||
            !TryReadAddresses(input, "to", required: true, out var to) ||
            !TryReadAddresses(input, "cc", required: false, out var cc) ||
            !TryReadAddresses(input, "bcc", required: false, out var bcc) ||
            !TryReadExactText(
                input,
                "subject",
                maximumUtf16Length: MailDraftPolicy.MaximumSubjectScalars * 2,
                out var subject) ||
            !TryReadExactText(
                input,
                "body",
                maximumUtf16Length: MailDraftPolicy.MaximumBodyScalars * 2,
                out var body))
            return false;

        draft = new MailDraftInput(to, cc, bcc, subject, body);
        return true;
    }

    private static bool TryReadAddresses(
        IReadOnlyDictionary<string, object?> input,
        string key,
        bool required,
        out IReadOnlyList<string> addresses)
    {
        addresses = [];
        if (!input.TryGetValue(key, out var raw)) return !required;

        if (raw is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumRecipientItems)
                return false;
            var parsed = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value)
                    return false;
                parsed.Add(value);
            }
            if (required && parsed.Count == 0) return false;
            addresses = parsed.AsReadOnly();
            return true;
        }

        if (raw is not IEnumerable sequence || raw is string) return false;
        var result = new List<string>();
        foreach (var item in sequence)
        {
            if (result.Count == MaximumRecipientItems || item is not string value) return false;
            result.Add(value);
        }
        if (required && result.Count == 0) return false;
        addresses = result.AsReadOnly();
        return true;
    }

    private static bool TryReadExactText(
        IReadOnlyDictionary<string, object?> input,
        string key,
        int maximumUtf16Length,
        out string value)
    {
        value = string.Empty;
        if (!input.TryGetValue(key, out var raw)) return false;
        var text = raw switch
        {
            string direct => direct,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (text is null || text.Length > maximumUtf16Length) return false;
        value = text;
        return true;
    }

    private static int CountRunes(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }

    private static WindowsToolExecutionResult Failure(string errorCode) =>
        new(
            false,
            new Dictionary<string, object?>(),
            "메일 초안 입력을 안전하게 검증하지 못했어요.",
            ErrorCode: errorCode);
}
