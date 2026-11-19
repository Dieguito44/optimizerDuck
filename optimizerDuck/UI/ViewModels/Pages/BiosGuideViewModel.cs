using CommunityToolkit.Mvvm.ComponentModel;
using optimizerDuck.Services.Bios;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>"BIOS guide" page: read-only steps for this PC's motherboard, when there is a guide.</summary>
public partial class BiosGuideViewModel(SystemInfoService systemInfoService) : ViewModel
{
    [ObservableProperty]
    private BiosGuide? _guide;

    [ObservableProperty]
    private string _boardText = string.Empty;

    [ObservableProperty]
    private string _matchText = string.Empty;

    public bool HasGuide => Guide is not null;

    protected override async Task InitializeOnceAsync()
    {
        var system = await systemInfoService.EnsureSnapshotAsync();
        var manufacturer = system.Firmware.BoardManufacturer;
        var board = system.Firmware.Motherboard;
        BoardText = Loc.Instance[
            "BiosGuide.Board",
            string.IsNullOrWhiteSpace(board)
                ? Loc.Instance["Report.NotMeasured"]
                : $"{manufacturer} {board}".Trim(),
            system.Firmware.BiosVersion ?? "—"
        ];

        // This PC's guide; otherwise the only one there is, marked as another board's.
        var own = BiosGuides.For(manufacturer, board);
        Guide = own ?? BiosGuides.All.FirstOrDefault();
        MatchText = own is not null
            ? Loc.Instance["BiosGuide.Match.Yes"]
            : Loc.Instance["BiosGuide.Match.No", Guide?.DisplayName ?? "—"];
        OnPropertyChanged(nameof(HasGuide));
    }
}
