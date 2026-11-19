using optimizerDuck.Common.Extensions;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.UI.ViewModels.Windows;

public partial class MainWindowViewModel : LocalizedObject
{
    /// <summary>Gets the application version displayed in the title bar.</summary>
    public string Version => $"v{Shared.FileVersion}";
}
