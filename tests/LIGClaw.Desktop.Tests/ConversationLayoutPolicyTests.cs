using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationLayoutPolicyTests
{
    [Fact]
    public void EmptyConversationUsesTheHeroComposerWithoutAnEmptyDocumentSurface()
    {
        var layout = ConversationLayoutPolicy.ForContent(hasContent: false);

        Assert.Equal(ConversationLayoutMode.Hero, layout.Mode);
        Assert.False(layout.ShowConversationSurface);
        Assert.True(layout.ShowExamples);
        Assert.Equal(1, layout.ComposerRow);
        Assert.Equal("HeroComposerWidth", layout.ComposerWidthResourceKey);
    }

    [Fact]
    public void ActiveConversationDocksTheComposerAndShowsTheDocumentSurface()
    {
        var layout = ConversationLayoutPolicy.ForContent(hasContent: true);

        Assert.Equal(ConversationLayoutMode.Docked, layout.Mode);
        Assert.True(layout.ShowConversationSurface);
        Assert.False(layout.ShowExamples);
        Assert.Equal(2, layout.ComposerRow);
        Assert.Equal("ConversationReadingWidth", layout.ComposerWidthResourceKey);
    }
}
