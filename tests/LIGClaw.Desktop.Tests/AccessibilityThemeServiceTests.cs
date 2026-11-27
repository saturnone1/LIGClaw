using System.Windows;
using LIGClaw.Desktop.Infrastructure.Shell;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;

namespace LIGClaw.Desktop.Tests;

public sealed class AccessibilityThemeServiceTests
{
    [Fact]
    public void HighContrastPaletteCoversEveryDeclaredSemanticResource()
    {
        var palette = HighContrastPalette.Create();

        Assert.DoesNotContain(HighContrastPalette.ResourceKeys, key => !palette.ContainsKey(key));
        Assert.All(palette.Values, brush => Assert.NotNull(brush));
    }

    [Fact]
    public void RestoresTheOriginalPaletteWhenHighContrastTurnsOff()
    {
        var resources = new ResourceDictionary();
        foreach (var key in HighContrastPalette.ResourceKeys) resources[key] = Brushes.Magenta;
        var highContrast = true;
        using var service = new AccessibilityThemeService(resources, () => highContrast, watchSystem: false);

        Assert.Same(SystemColors.WindowBrush, resources["CanvasBrush"]);
        highContrast = false;
        service.Apply();

        Assert.Same(Brushes.Magenta, resources["CanvasBrush"]);
        Assert.Same(Brushes.Magenta, resources["TextPrimaryBrush"]);
    }
}
