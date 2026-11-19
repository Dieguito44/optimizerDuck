using System.Text.RegularExpressions;

namespace optimizerDuck.Domain.Diagnostics.Reference;

/// <summary>How a table entry matches a part number or model.</summary>
public enum TableMatch
{
    Exact,
    Prefix,

    /// <summary>A regex; for RAM kits its first group may carry the speed.</summary>
    Regex,
}

/// <summary>A rated speed found for a RAM part number, and where it came from.</summary>
public sealed record RamKitMatch(int RatedMTps, string Source);

/// <summary>One RAM kit entry (ram_kits.json).</summary>
public sealed record RamKitEntry(
    TableMatch Match,
    string Pattern,
    int? RatedMTps,
    int Multiplier,
    string Source
);

/// <summary>One SSD endurance entry (ssd_tbw.json).</summary>
public sealed record SsdTbwEntry(TableMatch Match, string Pattern, int TbwTerabytes, string Source);

/// <summary>Latest stable BIOS of one board (bios_latest.json).</summary>
public sealed record BiosEntry(
    string Manufacturer,
    string Product,
    string Version,
    DateOnly Date,
    string Source,
    DateOnly Checked
);

/// <summary>
///     The reference tables shipped inside Zentra. Lookups never guess: no entry means the rule
///     reports "not in reference table".
/// </summary>
public sealed class ReferenceTables
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public ReferenceTables(
        IReadOnlyList<RamKitEntry> ramKits,
        IReadOnlyList<SsdTbwEntry> ssdTbw,
        IReadOnlyList<BiosEntry> biosLatest,
        string ramKitsUpdated = "",
        string ssdTbwUpdated = "",
        string biosLatestUpdated = ""
    )
    {
        RamKits = ramKits;
        SsdTbw = ssdTbw;
        BiosLatest = biosLatest;
        RamKitsUpdated = ramKitsUpdated;
        SsdTbwUpdated = ssdTbwUpdated;
        BiosLatestUpdated = biosLatestUpdated;
    }

    public static ReferenceTables Empty { get; } = new([], [], []);

    public IReadOnlyList<RamKitEntry> RamKits { get; }
    public IReadOnlyList<SsdTbwEntry> SsdTbw { get; }
    public IReadOnlyList<BiosEntry> BiosLatest { get; }
    public string RamKitsUpdated { get; }
    public string SsdTbwUpdated { get; }
    public string BiosLatestUpdated { get; }

    /// <summary>The rated speed of a RAM part number, or null when no entry knows it.</summary>
    public RamKitMatch? LookupRamKit(string? partNumber)
    {
        var part = Normalize(partNumber);
        if (part.Length == 0)
            return null;

        foreach (var entry in RamKits)
        {
            if (entry.Match != TableMatch.Regex)
            {
                if (Matches(entry.Match, entry.Pattern, part) && entry.RatedMTps is { } rated)
                    return new RamKitMatch(rated, entry.Source);
                continue;
            }

            Match match;
            try
            {
                match = Regex.Match(
                    part,
                    entry.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout
                );
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success)
                continue;

            var speed = entry.RatedMTps;
            if (match.Groups.Count > 1 && int.TryParse(match.Groups[1].Value, out var captured))
                speed = JedecSpeed(captured * Math.Max(1, entry.Multiplier));

            if (speed is >= 1600 and <= 10000)
                return new RamKitMatch(speed.Value, entry.Source);
        }

        return null;
    }

    /// <summary>The rated endurance (TBW) of an SSD model, or null when unknown.</summary>
    public SsdTbwEntry? LookupSsdTbw(string? model)
    {
        var text = Normalize(model);
        return text.Length == 0
            ? null
            : SsdTbw.FirstOrDefault(e => Matches(e.Match, e.Pattern, text));
    }

    /// <summary>The latest stable BIOS of a board, or null when the board is not in the table.</summary>
    public BiosEntry? LookupBios(string? manufacturer, string? product)
    {
        var maker = Normalize(manufacturer);
        var board = Normalize(product);
        if (maker.Length == 0 || board.Length == 0)
            return null;

        return BiosLatest.FirstOrDefault(e =>
            maker.Contains(Normalize(e.Manufacturer), StringComparison.Ordinal)
            && board == Normalize(e.Product)
        );
    }

    /// <summary>
    ///     Part numbers abbreviate speeds to two digits; three JEDEC speeds are not round
    ///     hundreds ("21" = 2133, "26" = 2666, "29" = 2933).
    /// </summary>
    internal static int JedecSpeed(int rounded) =>
        rounded switch
        {
            2100 => 2133,
            2600 => 2666,
            2900 => 2933,
            _ => rounded,
        };

    private static bool Matches(TableMatch match, string pattern, string value)
    {
        var p = Normalize(pattern);
        return match switch
        {
            TableMatch.Exact => value == p,
            TableMatch.Prefix => value.StartsWith(p, StringComparison.Ordinal),
            TableMatch.Regex => SafeRegex(value, pattern),
            _ => false,
        };
    }

    private static bool SafeRegex(string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(
                value,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout
            );
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Normalize(string? text) =>
        (text ?? string.Empty).Trim().ToUpperInvariant();
}
