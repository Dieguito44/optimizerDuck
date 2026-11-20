using System.Windows.Threading;
using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class OptimizePage : INavigableView<OptimizeViewModel>
{
    public OptimizePage(
        OptimizeViewModel viewModel,
        OptimizeOverviewViewModel overview,
        INavigationViewPageProvider pageProvider
    )
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        RootNavigation.SetPageProviderService(pageProvider);
        ViewModel.OptimizationsLoaded += OnOptimizationsLoaded;
        // "Ver ajustes" on a pack opens that category's tab.
        overview.OpenCategoryRequested += page => RootNavigation.Navigate(page);
    }

    public OptimizeViewModel ViewModel { get; }

    private void OnOptimizationsLoaded()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                var first = ViewModel.OptimizationCategories.FirstOrDefault();
                if (first?.TargetPageType != null)
                    RootNavigation.Navigate(first.TargetPageType);
            },
            DispatcherPriority.ContextIdle
        );
    }
}
