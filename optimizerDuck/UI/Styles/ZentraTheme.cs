using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace optimizerDuck.UI.Styles;

/// <summary>
///     Zentra's look: the teal-to-blue of the logo over slate surfaces, instead of the
///     original's yellow accent and Mica backdrop. Applied after every theme switch, because
///     WPF-UI replaces its theme dictionary each time; keys set on the application's own
///     resources win over the merged theme dictionary.
/// </summary>
public static class ZentraTheme
{
    private sealed record Palette(
        Color SystemAccent,
        Color PrimaryAccent,
        Color SecondaryAccent,
        Color TertiaryAccent,
        Color Window,
        Color Content,
        Color Card,
        Color CardSecondary,
        Color Stroke,
        Color Brand,
        Color BrandBlue,
        Color Critical,
        Color High,
        Color Warning,
        Color Good
    );

    private static readonly Palette Dark = new(
        SystemAccent: Rgb(0x0D, 0x94, 0x88),
        PrimaryAccent: Rgb(0x2D, 0xD4, 0xBF),
        SecondaryAccent: Rgb(0x5E, 0xEA, 0xD4),
        TertiaryAccent: Rgb(0x99, 0xF6, 0xE4),
        Window: Rgb(0x0F, 0x14, 0x19),
        Content: Rgb(0x12, 0x1A, 0x21),
        Card: Rgb(0x18, 0x21, 0x29),
        CardSecondary: Rgb(0x1D, 0x27, 0x31),
        Stroke: Rgb(0x26, 0x32, 0x3D),
        Brand: Rgb(0x2D, 0xD4, 0xBF),
        BrandBlue: Rgb(0x60, 0xA5, 0xFA),
        Critical: Rgb(0xF8, 0x71, 0x71),
        High: Rgb(0xFB, 0x92, 0x3C),
        Warning: Rgb(0xFB, 0xBF, 0x24),
        Good: Rgb(0x34, 0xD3, 0x99)
    );

    private static readonly Palette Light = new(
        SystemAccent: Rgb(0x0F, 0x76, 0x6E),
        PrimaryAccent: Rgb(0x0F, 0x76, 0x6E),
        SecondaryAccent: Rgb(0x11, 0x5E, 0x59),
        TertiaryAccent: Rgb(0x13, 0x4E, 0x4A),
        Window: Rgb(0xF3, 0xF6, 0xF8),
        Content: Rgb(0xF8, 0xFA, 0xFB),
        Card: Rgb(0xFF, 0xFF, 0xFF),
        CardSecondary: Rgb(0xF1, 0xF5, 0xF8),
        Stroke: Rgb(0xDC, 0xE3, 0xE8),
        Brand: Rgb(0x0F, 0x76, 0x6E),
        BrandBlue: Rgb(0x1D, 0x4E, 0xD8),
        Critical: Rgb(0xB9, 0x1C, 0x1C),
        High: Rgb(0xC2, 0x41, 0x0C),
        Warning: Rgb(0xB4, 0x53, 0x09),
        Good: Rgb(0x04, 0x78, 0x57)
    );

    /// <summary>Applies the accent and surface palette for the given theme.</summary>
    public static void Apply(ApplicationTheme theme)
    {
        if (theme == ApplicationTheme.HighContrast)
            return; // Windows' high-contrast colors win: they are an accessibility setting.

        var p = theme == ApplicationTheme.Light ? Light : Dark;
        ApplicationAccentColorManager.Apply(
            systemAccent: p.SystemAccent,
            primaryAccent: p.PrimaryAccent,
            secondaryAccent: p.SecondaryAccent,
            tertiaryAccent: p.TertiaryAccent
        );

        var resources = Application.Current?.Resources;
        if (resources is null)
            return;

        SetColor(resources, "ApplicationBackground", p.Window);
        Set(resources, "NavigationViewContentBackground", p.Content);
        Set(resources, "NavigationViewContentGridBorderBrush", p.Stroke);
        SetColor(resources, "CardBackgroundFillColorDefault", p.Card);
        SetColor(resources, "CardBackgroundFillColorSecondary", p.CardSecondary);
        SetColor(resources, "CardStrokeColorDefault", p.Stroke);
        Set(resources, "NavigationViewSelectionIndicatorForeground", p.Brand);

        // Zentra's own brushes, used by the pages.
        Set(resources, "ZentraBrandBrush", p.Brand);
        Set(resources, "ZentraBrandMutedBrush", WithAlpha(p.Brand, 0x24));
        Set(resources, "ZentraBlueBrush", p.BrandBlue);
        Set(resources, "ZentraCriticalBrush", p.Critical);
        Set(resources, "ZentraWarningBrush", p.Warning);
        Set(resources, "ZentraGoodBrush", p.Good);
        Set(resources, "ZentraHighBrush", p.High);
        Set(resources, "ZentraCardBrush", p.Card);
        Set(resources, "ZentraStrokeBrush", p.Stroke);
        resources["ZentraBrandGradientBrush"] = Frozen(
            new LinearGradientBrush(p.Brand, p.BrandBlue, new Point(0, 0), new Point(1, 1))
        );

        RefreshToggleSwitchBrushes(resources);
    }

    /// <summary>The on-state of toggle switches follows the accent, not WPF-UI's default.</summary>
    private static void RefreshToggleSwitchBrushes(ResourceDictionary resources)
    {
        Color Get(string key, Color fallback) => resources[key] is Color c ? c : fallback;

        var primary = Get("SystemAccentColorPrimary", Dark.PrimaryAccent);
        var secondary = Get("AccentFillColorSecondary", Dark.SecondaryAccent);
        var tertiary = Get("AccentFillColorTertiary", Dark.TertiaryAccent);
        var knobOn = Get("TextOnAccentFillColorPrimary", Colors.White);

        Set(resources, "ToggleSwitchFillOn", primary);
        Set(resources, "ToggleSwitchFillOnPointerOver", secondary);
        Set(resources, "ToggleSwitchFillOnPressed", tertiary);
        Set(resources, "ToggleSwitchStrokeOn", primary);
        Set(resources, "ToggleSwitchStrokeOnPointerOver", secondary);
        Set(resources, "ToggleSwitchStrokeOnPressed", tertiary);
        Set(resources, "ToggleSwitchKnobFillOn", knobOn);
        Set(resources, "ToggleSwitchKnobFillOnPointerOver", knobOn);
        Set(resources, "ToggleSwitchKnobFillOnPressed", knobOn);
    }

    /// <summary>Sets both the "{name}Color" and the "{name}Brush" keys, as WPF-UI defines them.</summary>
    private static void SetColor(ResourceDictionary resources, string name, Color color)
    {
        resources[name + "Color"] = color;
        Set(resources, name + "Brush", color);
    }

    private static void Set(ResourceDictionary resources, string key, Color color) =>
        resources[key] = Frozen(new SolidColorBrush(color));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
