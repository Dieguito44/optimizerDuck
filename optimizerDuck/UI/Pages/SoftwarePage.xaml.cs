using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class SoftwarePage : INavigableView<SoftwareViewModel>
{
    public SoftwarePage(SoftwareViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    public SoftwareViewModel ViewModel { get; }
}
