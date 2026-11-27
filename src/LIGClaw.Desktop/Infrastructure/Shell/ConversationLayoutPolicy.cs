namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum ConversationLayoutMode
{
    Hero,
    Docked,
}

internal readonly record struct ConversationLayoutMetrics(
    ConversationLayoutMode Mode,
    bool ShowConversationSurface,
    bool ShowExamples,
    int ComposerRow,
    string ComposerWidthResourceKey);

internal static class ConversationLayoutPolicy
{
    internal static ConversationLayoutMetrics ForContent(bool hasContent) => hasContent
        ? new ConversationLayoutMetrics(
            ConversationLayoutMode.Docked,
            ShowConversationSurface: true,
            ShowExamples: false,
            ComposerRow: 2,
            ComposerWidthResourceKey: "ConversationReadingWidth")
        : new ConversationLayoutMetrics(
            ConversationLayoutMode.Hero,
            ShowConversationSurface: false,
            ShowExamples: true,
            ComposerRow: 1,
            ComposerWidthResourceKey: "HeroComposerWidth");
}
