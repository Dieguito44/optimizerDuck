using System.Windows;
using System.Windows.Controls;
using optimizerDuck.Services.Configuration;

namespace optimizerDuck.UI.Windows;

/// <summary>
///     Asks for the client's written consent before a phase 7 option. "Apply" stays disabled until
///     the technician ticks that the client agreed in writing. A plain top-most window, like the
///     refresh rate confirmation, because the run's progress dialog is already open.
/// </summary>
public sealed class ConsentWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new();

    private ConsentWindow(string title, string body)
    {
        Title = title;
        Topmost = true;
        // The theme's colors: without them the window stayed white while its checkbox and
        // buttons took the dark theme's light text (found in the Sandbox tests, 19/11/2026).
        ThemedWindow.Apply(this);
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var agreed = new CheckBox
        {
            Content = Loc.Instance["Consent.Checkbox"],
            Margin = new Thickness(0, 16, 0, 0),
        };
        var apply = new Button
        {
            Content = Loc.Instance["Consent.Apply"],
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
        };
        var cancel = new Button
        {
            Content = Loc.Instance["Button.Cancel"],
            Padding = new Thickness(16, 6, 16, 6),
            IsCancel = true,
        };
        agreed.Checked += (_, _) => apply.IsEnabled = true;
        agreed.Unchecked += (_, _) => apply.IsEnabled = false;
        apply.Click += (_, _) => Finish(true);
        cancel.Click += (_, _) => Finish(false);
        Closed += (_, _) => Finish(false);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            MaxWidth = 520,
            Children =
            {
                new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                },
                agreed,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { apply, cancel },
                },
            },
        };
    }

    public static Task<bool> AskAsync(string title, string body)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.FromResult(false);
        return dispatcher
            .InvokeAsync(() =>
            {
                var window = new ConsentWindow(title, body);
                window.Show();
                window.Activate();
                return window._answer.Task;
            })
            .Task.Unwrap();
    }

    private void Finish(bool agreed)
    {
        if (!_answer.TrySetResult(agreed))
            return;
        if (IsVisible)
            Close();
    }
}
