using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Common.Converters;

/// <summary>Maps a progress percentage to the brand color below 50%, warning below 80%, critical above.</summary>
public class ProgressBarValueToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0x0, 0x0, 0x0, 0x0));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double percent)
            return DefaultBrush;

        return percent switch
        {
            < 0 => DefaultBrush,
            // Zentra palette: the brand color while there is room, then warning and critical.
            < 50 => ThemeResource.Get<Brush>("ZentraBrandBrush")
                ?? new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)),
            < 80 => ThemeResource.Get<Brush>("ZentraWarningBrush")
                ?? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
            _ => ThemeResource.Get<Brush>("ZentraCriticalBrush")
                ?? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        };
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        throw new NotImplementedException();
    }
}
