using optimizerDuck.Services.Configuration;

namespace optimizerDuck.Services.Bios;

/// <summary>Whether a step's menu path comes from the board's official manual.</summary>
public enum BiosStepSource
{
    /// <summary>Taken from the official manual (page cited).</summary>
    Manual,

    /// <summary>The manual does not document it: no path is given until it is verified.</summary>
    NotDocumented,
}

/// <summary>One step of a BIOS guide. Texts are resource keys, shown in the UI language.</summary>
public sealed record BiosGuideStep(string Key, BiosStepSource Source, int? ManualPage)
{
    public string Title => Loc.Instance[$"BiosGuide.{Key}.Title"];
    public string Body => Loc.Instance[$"BiosGuide.{Key}.Body"];
    public bool IsDocumented => Source == BiosStepSource.Manual;
}

/// <summary>A guide for one motherboard model.</summary>
public sealed record BiosGuide(
    string Manufacturer,
    string Model,
    string ManualUrl,
    string ManualDate,
    IReadOnlyList<BiosGuideStep> Steps
)
{
    public string DisplayName => $"{Manufacturer} {Model}";

    public bool Matches(string? boardManufacturer, string? board) =>
        board is not null
        && board.Contains(Model, StringComparison.OrdinalIgnoreCase)
        && (
            boardManufacturer is null
            || boardManufacturer.Contains(Manufacturer, StringComparison.OrdinalIgnoreCase)
        );
}

/// <summary>
///     BIOS guides (plan phase 6, step 4). Zentra never changes the BIOS: the guide tells the
///     technician or the client where to go. Only paths that the official manual documents are
///     given; nothing is guessed.
/// </summary>
public static class BiosGuides
{
    /// <summary>
    ///     ASRock B450M-HDV R4.0, from its official User Manual (English, PDF of 31/05/2021,
    ///     download.asrock.com/Manual/B450M-HDV%20R4.0.pdf), chapter 4. The manual warns that UEFI
    ///     screens change with updates, and it does not document XMP or the UMA frame buffer:
    ///     those steps say so instead of guessing a path.
    /// </summary>
    public static readonly BiosGuide AsrockB450mHdvR40 = new(
        "ASRock",
        "B450M-HDV R4.0",
        "https://download.asrock.com/Manual/B450M-HDV%20R4.0.pdf",
        "31/05/2021",
        [
            new("Enter", BiosStepSource.Manual, 44),
            new("RamSpeed", BiosStepSource.Manual, 48),
            new("Xmp", BiosStepSource.NotDocumented, null),
            new("Uma", BiosStepSource.NotDocumented, null),
            new("Save", BiosStepSource.Manual, 72),
            new("Recover", BiosStepSource.Manual, 72),
        ]
    );

    public static readonly IReadOnlyList<BiosGuide> All = [AsrockB450mHdvR40];

    public static BiosGuide? For(string? boardManufacturer, string? board) =>
        All.FirstOrDefault(g => g.Matches(boardManufacturer, board));
}
