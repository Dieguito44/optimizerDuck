using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using optimizerDuck.Services.Configuration;

namespace optimizerDuck.UI.Windows;

/// <summary>
///     "Do you see the picture?" after a refresh rate change, with a countdown. It is a plain
///     top-most window, not a content dialog: the progress dialog of the run is already open, and
///     the answer must come from the person at the PC (the technician on RustDesk may see a
///     picture the monitor does not show). No answer means "go back".
/// </summary>
public sealed class DisplayConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new();
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _countdown;
    private int _secondsLeft;

    private DisplayConfirmWindow(string monitor, TimeSpan timeout)
    {
        _secondsLeft = (int)timeout.TotalSeconds;
        Title = Loc.Instance["DisplayConfirm.Title"];
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        _countdown = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 14 };
        var keep = new Button
        {
            Content = Loc.Instance["DisplayConfirm.Keep"],
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = false,
        };
        var back = new Button
        {
            Content = Loc.Instance["DisplayConfirm.Back"],
            Padding = new Thickness(16, 6, 16, 6),
            IsCancel = true,
        };
        keep.Click += (_, _) => Finish(true);
        back.Click += (_, _) => Finish(false);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = Loc.Instance["DisplayConfirm.Message", monitor],
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15,
                },
                _countdown,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { keep, back },
                },
            },
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            UpdateCountdown();
            if (_secondsLeft <= 0)
                Finish(false);
        };
        Closed += (_, _) => Finish(false);
        UpdateCountdown();
    }

    /// <summary>Shows the question on the UI thread and returns the answer (false on timeout).</summary>
    public static Task<bool> AskAsync(string monitor, TimeSpan timeout)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.FromResult(false);
        return dispatcher
            .InvokeAsync(() =>
            {
                var window = new DisplayConfirmWindow(monitor, timeout);
                window.Show();
                window.Activate();
                window._timer.Start();
                return window._answer.Task;
            })
            .Task.Unwrap();
    }

    private void UpdateCountdown() =>
        _countdown.Text = Loc.Instance["DisplayConfirm.Countdown", Math.Max(0, _secondsLeft)];

    private void Finish(bool keep)
    {
        if (!_answer.TrySetResult(keep))
            return;
        _timer.Stop();
        if (IsVisible)
            Close();
    }
}
