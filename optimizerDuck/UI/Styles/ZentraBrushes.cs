using System.Windows;
using System.Windows.Media;
using optimizerDuck.Domain.Diagnostics;

namespace optimizerDuck.UI.Styles;

/// <summary>
///     Brushes picked in code (score, severity) from the palette of the active theme, so the
///     pages and the Home card use the same colors as <see cref="ZentraTheme" />.
/// </summary>
public static class ZentraBrushes
{
    /// <summary>Good (85 or more), fair (60 or more) or poor.</summary>
    public static Brush ForScore(int score) =>
        Find(
            score switch
            {
                >= 85 => "ZentraGoodBrush",
                >= 60 => "ZentraWarningBrush",
                _ => "ZentraCriticalBrush",
            }
        );

    public static Brush ForSeverity(Severity severity) =>
        Find(
            severity switch
            {
                Severity.Critical => "ZentraCriticalBrush",
                Severity.High => "ZentraHighBrush",
                Severity.Medium => "ZentraWarningBrush",
                Severity.Low => "ZentraBlueBrush",
                _ => "TextFillColorTertiaryBrush",
            }
        );

    /// <summary>The same color, faint, for the background of a badge.</summary>
    public static Brush Tint(Brush brush)
    {
        if (brush is not SolidColorBrush solid)
            return Brushes.Transparent;
        var tint = new SolidColorBrush(
            Color.FromArgb(0x2E, solid.Color.R, solid.Color.G, solid.Color.B)
        );
        tint.Freeze();
        return tint;
    }

    private static Brush Find(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
