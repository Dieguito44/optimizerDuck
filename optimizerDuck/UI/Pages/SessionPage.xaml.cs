using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class SessionPage : INavigableView<SessionViewModel>
{
    public SessionPage(SessionViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    public SessionViewModel ViewModel { get; }
}
