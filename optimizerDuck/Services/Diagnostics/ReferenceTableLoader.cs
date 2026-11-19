using System.Globalization;
using Newtonsoft.Json.Linq;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>
///     Loads the reference tables embedded in the executable. They ship signed with the app, so a
///     client PC cannot alter them. A malformed table fails loudly in tests, never silently here.
/// </summary>
public static class ReferenceTableLoader
{
    public const int SupportedSchema = 1;

    private static readonly Lazy<ReferenceTables> _embedded = new(LoadEmbeddedCore);

    /// <summary>The embedded tables, parsed once.</summary>
    public static ReferenceTables Embedded => _embedded.Value;

    private static ReferenceTables LoadEmbeddedCore()
    {
        var ram = Parse(ReadRequired("Diagnostics/ram_kits.json"));
        var ssd = Parse(ReadRequired("Diagnostics/ssd_tbw.json"));
        var bios = Parse(ReadRequired("Diagnostics/bios_latest.json"));

        return new ReferenceTables(
            ParseRamKits(ram),
            ParseSsdTbw(ssd),
            ParseBios(bios),
            (string?)ram["updated"] ?? string.Empty,
            (string?)ssd["updated"] ?? string.Empty,
            (string?)bios["updated"] ?? string.Empty
        );
    }

    private static string ReadRequired(string path) =>
        EmbeddedResourceHelper.ReadText(path)
        ?? throw new InvalidOperationException($"Embedded reference table missing: {path}");

    internal static JObject Parse(string json)
    {
        var root = JObject.Parse(json);
        var schema = (int?)root["schema"];
        if (schema != SupportedSchema)
            throw new InvalidOperationException(
                $"Unsupported reference table schema {schema}; expected {SupportedSchema}."
            );
        return root;
    }

    internal static IReadOnlyList<RamKitEntry> ParseRamKits(JObject root) =>
        Entries(root)
            .Select(e => new RamKitEntry(
                ParseMatch(e),
                Required(e, "pattern"),
                (int?)e["rated_mts"],
                (int?)e["multiplier"] ?? 1,
                Required(e, "source")
            ))
            .ToList();

    internal static IReadOnlyList<SsdTbwEntry> ParseSsdTbw(JObject root) =>
        Entries(root)
            .Select(e => new SsdTbwEntry(
                ParseMatch(e),
                Required(e, "pattern"),
                (int?)e["tbw_tb"] ?? throw new InvalidOperationException("tbw_tb missing"),
                Required(e, "source")
            ))
            .ToList();

    internal static IReadOnlyList<BiosEntry> ParseBios(JObject root) =>
        Entries(root)
            .Select(e => new BiosEntry(
                Required(e, "manufacturer"),
                Required(e, "product"),
                Required(e, "version"),
                ParseDate(Required(e, "date")),
                Required(e, "source"),
                ParseDate(Required(e, "checked"))
            ))
            .ToList();

    private static IEnumerable<JObject> Entries(JObject root) =>
        (
            root["entries"] as JArray ?? throw new InvalidOperationException("entries missing")
        ).OfType<JObject>();

    private static TableMatch ParseMatch(JObject entry) =>
        Required(entry, "match") switch
        {
            "exact" => TableMatch.Exact,
            "prefix" => TableMatch.Prefix,
            "regex" => TableMatch.Regex,
            var other => throw new InvalidOperationException($"Unknown match kind '{other}'."),
        };

    private static string Required(JObject entry, string name) =>
        (string?)entry[name] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Reference table field '{name}' missing.");

    private static DateOnly ParseDate(string text) =>
        DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
