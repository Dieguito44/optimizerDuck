using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class RepairPage : INavigableView<RepairViewModel>
{
    public RepairPage(RepairViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    public RepairViewModel ViewModel { get; }
}
