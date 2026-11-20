using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Configuration;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Customize;
using optimizerDuck.Services.UI;
using optimizerDuck.UI.Controls;
using optimizerDuck.UI.Dialogs;
using optimizerDuck.UI.Pages;
using optimizerDuck.UI.ViewModels.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace optimizerDuck.UI.Windows;

public partial class MainWindow : IWindow
{
    private readonly ILogger<MainWindow> _logger;
    private readonly IContentDialogService _contentDialogService;
    private readonly IOptionsMonitor<AppSettings> _appOptionsMonitor;
    private readonly ConfigManager _configManager;

    public MainWindow(
        MainWindowViewModel viewModel,
        ConfigManager configManager,
        INavigationService navigationService,
        IContentDialogService contentDialogService,
        INavigationViewPageProvider pageProvider,
        IOptionsMonitor<AppSettings> appOptionsMonitor,
        ISnackbarService snackbarService,
        CustomizeRegistry customizeRegistry,
        ILogger<MainWindow> logger
    )
    {
        _logger = logger;
        _contentDialogService = contentDialogService;
        _appOptionsMonitor = appOptionsMonitor;
        _configManager = configManager;
        InitializeComponent();

        // Zentra paints its own slate background (ZentraTheme) instead of Mica, so the
        // window looks the same on every PC and wallpaper.
        WindowBackdropType = WindowBackdropType.None;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

        DataContext = viewModel;

        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(RootContentDialogPresenter);
        navigationService.SetNavigationControl(RootNavigation);

        RootNavigation.SetPageProviderService(pageProvider);

        RootNavigation.Loaded += OnRootNavigationLoaded;

        // Redesign (19/11/2026): the tabs live in Zentra's own top bar. Any navigation (a tab,
        // a button inside a page) marks the tab of the page that opened.
        RootNavigation.Navigated += (_, args) => MarkCurrentTab(args.Page?.GetType());

        LogList.ItemsSource = ActivityLog.Entries;
        ActivityLog.Added += OnActivityAdded;
        if (ActivityLog.Entries.Count > 0)
            OnActivityAdded(ActivityLog.Entries[^1]);
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Type page })
            RootNavigation.Navigate(page);
    }

    private void MarkCurrentTab(Type? page)
    {
        if (page is null)
            return;
        foreach (var tab in FindTabs(this))
            tab.IsChecked = tab.Tag is Type t && t == page;
    }

    private static IEnumerable<RadioButton> FindTabs(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton { Tag: Type } tab)
                yield return tab;
            else if (child is not NavigationView)
                foreach (var nested in FindTabs(child))
                    yield return nested;
        }
    }

    private void OnActivityAdded(ActivityEntry entry)
    {
        LogLastLine.Text = $"{entry.TimeText}  {entry.Text}";
        LogScroll.ScrollToEnd();
    }

    private void OnLogHeaderClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var open = LogScroll.Visibility != Visibility.Visible;
        LogScroll.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LogToggleText.Text = Loc.Instance[open ? "Activity.Hide" : "Activity.Show"];
        if (open)
            LogScroll.ScrollToEnd();
    }

    internal void UpdatePendingIndicator(bool hasPending)
    {
        var title = hasPending ? $"{Shared.AppName}*" : Shared.AppName;
        AppTitleText.Text = title;
        Title = title;
    }

    private async void OnRootNavigationLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RootNavigation.Loaded -= OnRootNavigationLoaded;

            if (!_appOptionsMonitor.CurrentValue.App.LegalAccepted)
            {
                var legalDialog =
                    (Application.Current as App)?.AppHost?.Services.GetService<LegalDialog>()
                    ?? new LegalDialog();

                var dialog = new ContentDialog
                {
                    Content = legalDialog,
                    DefaultButton = ContentDialogButton.Primary,
                };

                // Bind to Loc so title/buttons update when language changes inside the dialog
                dialog.SetBinding(
                    ContentDialog.TitleProperty,
                    new Binding("[LegalDialog.Title]")
                    {
                        Source = Loc.Instance,
                        Mode = BindingMode.OneWay,
                    }
                );
                dialog.SetBinding(
                    ContentDialog.PrimaryButtonTextProperty,
                    new Binding("[Button.Accept]")
                    {
                        Source = Loc.Instance,
                        Mode = BindingMode.OneWay,
                    }
                );
                dialog.SetBinding(
                    ContentDialog.CloseButtonTextProperty,
                    new Binding("[Button.Close]")
                    {
                        Source = Loc.Instance,
                        Mode = BindingMode.OneWay,
                    }
                );

                var result = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
                if (result == ContentDialogResult.Primary)
                {
                    await _configManager.SetAsync(x => x.App.LegalAccepted, true);
                }
                else
                {
                    Close();
                    return;
                }
            }

            RootNavigation.Navigate(typeof(DashboardPage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during navigation initialization");
        }
    }
}
