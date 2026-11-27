namespace LIGClaw.Domain;

public sealed record SubagentTaskDraft(string Title, string Prompt);

public sealed record SubagentBatchDraft(
    string ParentConversationId,
    string ParentRunId,
    string ParentToolCallId,
    IReadOnlyList<SubagentTaskDraft> Tasks,
    string? ModelProfileId,
    string MaxRisk,
    int MaxRuntimeSeconds,
    int ResultMaxCharacters,
    string Reason);

public static class SubagentPolicy
{
    public static bool IsValid(SubagentBatchDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.ParentConversationId.Length is >= 1 and <= 160 &&
               draft.ParentRunId.Length is >= 1 and <= 160 &&
               draft.ParentToolCallId.Length is >= 1 and <= 160 &&
               draft.Tasks.Count is >= 1 and <= 4 &&
               draft.Tasks.All(task => task.Title.Length is >= 1 and <= 80 && task.Prompt.Length is >= 1 and <= 4_000) &&
               (draft.ModelProfileId is null || draft.ModelProfileId.Length is >= 1 and <= 64 &&
                   draft.ModelProfileId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) &&
               draft.MaxRisk is "R0" or "R1" &&
               draft.MaxRuntimeSeconds is >= 30 and <= 900 &&
               draft.ResultMaxCharacters is >= 1_000 and <= 20_000 &&
               draft.Reason.Length is >= 1 and <= 160;
    }
}
