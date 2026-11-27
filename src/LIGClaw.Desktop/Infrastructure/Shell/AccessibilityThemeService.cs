using System.ComponentModel;
using System.Windows;
using Brush = System.Windows.Media.Brush;
using SystemColors = System.Windows.SystemColors;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class AccessibilityThemeService : IDisposable
{
    private readonly ResourceDictionary _resources;
    private readonly Dictionary<string, object> _defaults = new(StringComparer.Ordinal);
    private readonly Func<bool> _isHighContrast;
    private readonly bool _watchSystem;

    internal AccessibilityThemeService(
        ResourceDictionary resources,
        Func<bool>? isHighContrast = null,
        bool watchSystem = true)
    {
        _resources = resources;
        _isHighContrast = isHighContrast ?? (() => SystemParameters.HighContrast);
        _watchSystem = watchSystem;
        foreach (var key in HighContrastPalette.ResourceKeys)
        {
            if (_resources[key] is { } value) _defaults[key] = value;
        }
        if (_watchSystem) SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        Apply();
    }

    internal void Apply()
    {
        if (_isHighContrast())
        {
            foreach (var (key, value) in HighContrastPalette.Create()) _resources[key] = value;
            return;
        }

        foreach (var (key, value) in _defaults) _resources[key] = value;
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Apply();
    }

    public void Dispose()
    {
        if (_watchSystem) SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
    }
}

internal static class HighContrastPalette
{
    internal static readonly string[] ResourceKeys =
    [
        "CanvasBrush", "SurfaceBrush", "SurfaceElevatedBrush", "SurfaceMutedBrush", "SurfaceSunkenBrush",
        "BrandMistBrush", "BrandIceBrush", "BorderBrush", "BorderStrongBrush", "DividerBrush",
        "TextPrimaryBrush", "TextSecondaryBrush", "InactiveBrush", "BrandBlueBrush", "BrandBlueHoverBrush",
        "BrandBluePressedBrush", "FocusBrush", "InfoBrush", "InfoSubtleBrush", "SuccessBrush",
        "SuccessSubtleBrush", "WarningBrush", "WarningSubtleBrush", "DangerBrush", "DangerSubtleBrush",
        "TextOnBrandBrush", "NavigationTextBrush", "NavigationTextMutedBrush", "NavigationHoverBrush",
        "NavigationSelectedBrush", "NavigationLogoBackdropBrush",
    ];

    internal static IReadOnlyDictionary<string, Brush> Create()
    {
        var result = new Dictionary<string, Brush>(StringComparer.Ordinal);
        Add(result, SystemColors.WindowBrush,
            "CanvasBrush", "SurfaceBrush", "SurfaceElevatedBrush", "BrandMistBrush", "BrandIceBrush");
        Add(result, SystemColors.ControlBrush, "SurfaceMutedBrush", "SurfaceSunkenBrush");
        Add(result, SystemColors.WindowTextBrush,
            "BorderBrush", "BorderStrongBrush", "DividerBrush", "TextPrimaryBrush", "TextSecondaryBrush");
        result["InactiveBrush"] = SystemColors.GrayTextBrush;
        Add(result, SystemColors.HighlightBrush,
            "BrandBlueBrush", "BrandBlueHoverBrush", "BrandBluePressedBrush", "FocusBrush", "InfoBrush",
            "SuccessBrush", "WarningBrush", "DangerBrush", "NavigationHoverBrush", "NavigationSelectedBrush",
            "NavigationLogoBackdropBrush");
        Add(result, SystemColors.ControlBrush,
            "InfoSubtleBrush", "SuccessSubtleBrush", "WarningSubtleBrush", "DangerSubtleBrush");
        Add(result, SystemColors.HighlightTextBrush, "TextOnBrandBrush", "NavigationTextBrush");
        result["NavigationTextMutedBrush"] = SystemColors.HighlightTextBrush;
        return result;
    }

    private static void Add(IDictionary<string, Brush> destination, Brush brush, params string[] keys)
    {
        foreach (var key in keys) destination[key] = brush;
    }
}
