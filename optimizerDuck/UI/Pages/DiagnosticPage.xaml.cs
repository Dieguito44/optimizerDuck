using optimizerDuck.UI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace optimizerDuck.UI.Pages;

public partial class DiagnosticPage : INavigableView<DiagnosticViewModel>
{
    public DiagnosticPage(DiagnosticViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    public DiagnosticViewModel ViewModel { get; }
}
