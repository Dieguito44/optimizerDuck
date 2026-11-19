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
        DataObject.AddPastingHandler(KeyBox, (_, _) => Dispatcher.BeginInvoke(ClearClipboard));
    }

    /// <summary>
    ///     Empties the clipboard and, when Win+V history is on, the history too: otherwise the key
    ///     stays there (and in the cloud copy, if the client syncs it). The history loses its
    ///     unpinned items; the help text on the page says so.
    /// </summary>
    private static void ClearClipboard()
    {
        try
        {
            Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // Another app holds the clipboard; the history is still cleared below.
        }

        try
        {
            if (global::Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled())
                global::Windows.ApplicationModel.DataTransfer.Clipboard.ClearHistory();
        }
        catch (Exception)
        {
            // Not available on this Windows build, or blocked by policy: nothing else to do.
        }
    }

    public SessionViewModel ViewModel { get; }
}
