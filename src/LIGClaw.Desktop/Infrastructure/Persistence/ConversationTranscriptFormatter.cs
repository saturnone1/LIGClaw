using System.Text;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal static class ConversationTranscriptFormatter
{
    public static string Format(IEnumerable<ConversationTurn> turns)
    {
        var transcript = new StringBuilder();
        foreach (var turn in turns)
        {
            AppendTurnStart(transcript, turn.UserInput);
            transcript.Append(turn.AssistantText);
            AppendTerminalStatus(transcript, turn.Status, string.IsNullOrWhiteSpace(turn.AssistantText));
        }
        return transcript.ToString();
    }

    public static void AppendTurnStart(StringBuilder transcript, string userInput)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        if (transcript.Length > 0 && !transcript.ToString().EndsWith("\n\n", StringComparison.Ordinal))
            transcript.AppendLine().AppendLine();
        if (transcript.Length > 0) transcript.AppendLine("---").AppendLine();
        transcript.AppendLine("### 나");
        foreach (var line in userInput.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            transcript.Append("> ").AppendLine(line);
        transcript.AppendLine().AppendLine("### LIGClaw");
    }

    private static void AppendTerminalStatus(StringBuilder transcript, string status, bool answerIsEmpty)
    {
        var label = status switch
        {
            "cancelled" => "요청을 중단했습니다.",
            "failed" => "요청을 처리하지 못했습니다.",
            "interrupted" => "앱 종료로 요청이 중단됐습니다.",
            _ => null,
        };
        if (label is null) return;
        if (!answerIsEmpty) transcript.AppendLine().AppendLine();
        transcript.Append("_").Append(label).AppendLine("_");
    }
}
