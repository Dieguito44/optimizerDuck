using System.Windows;

namespace optimizerDuck.UI.Windows;

/// <summary>
///     The app theme's background and text color for a plain WPF window. Without it the window
///     keeps WPF's white background while the controls inside take the theme's styles, so in the
///     dark theme their light text sits on white and cannot be read.
/// </summary>
public static class ThemedWindow
{
    public const string BackgroundKey = "ApplicationBackgroundBrush";
    public const string ForegroundKey = "TextFillColorPrimaryBrush";

    public static void Apply(Window window)
    {
        window.SetResourceReference(Window.BackgroundProperty, BackgroundKey);
        window.SetResourceReference(Window.ForegroundProperty, ForegroundKey);
    }
}
