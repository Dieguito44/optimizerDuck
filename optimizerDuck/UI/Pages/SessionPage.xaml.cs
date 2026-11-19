using System.Runtime.InteropServices;
using System.Windows;
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

        // After the key is pasted, empty the clipboard so it does not stay on the client's PC.
        DataObject.AddPastingHandler(
            KeyBox,
            (_, _) =>
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        Clipboard.Clear();
                    }
                    catch (ExternalException)
                    {
                        // Another app holds the clipboard; nothing else to do.
                    }
                })
        );
    }

    public SessionViewModel ViewModel { get; }
}
