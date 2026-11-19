using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class BiosGuidePage : INavigableView<BiosGuideViewModel>
{
    public BiosGuidePage(BiosGuideViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    public BiosGuideViewModel ViewModel { get; }
}
