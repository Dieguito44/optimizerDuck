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
                : BoardName(manufacturer, board),
            system.Firmware.BiosVersion ?? "—"
        ];

        // Only this PC's own guide. Another board's steps, even "as a reference", can send a
        // technician to the wrong menu on a client's PC (found in the Sandbox tests, 19/11/2026).
        Guide = BiosGuides.For(manufacturer, board);
        MatchText = Guide is not null
            ? Loc.Instance["BiosGuide.Match.Yes"]
            : Loc.Instance["BiosGuide.Match.None"];
        OnPropertyChanged(nameof(HasGuide));
    }

    /// <summary>
    ///     The maker and the board, without repeating the maker when the board's name already
    ///     starts with it ("ASRock" + "ASRock B450M-HDV" is "ASRock B450M-HDV").
    /// </summary>
    internal static string BoardName(string? manufacturer, string board) =>
        string.IsNullOrWhiteSpace(manufacturer)
        || board.StartsWith(manufacturer.Trim(), StringComparison.OrdinalIgnoreCase)
            ? board.Trim()
            : $"{manufacturer.Trim()} {board.Trim()}";
}
