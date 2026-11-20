using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

/// <summary>The "Resumen" tab of Optimize (redesign, 19/11/2026).</summary>
public partial class OptimizeOverviewPage : INavigableView<OptimizeOverviewViewModel>
{
    public OptimizeOverviewPage(OptimizeOverviewViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    public OptimizeOverviewViewModel ViewModel { get; }
}
