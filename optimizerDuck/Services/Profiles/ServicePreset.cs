using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.UI;

namespace optimizerDuck.Services.Profiles;

/// <summary>
///     Plan of improvements F3-01: a named list of settings (by GUID) and apps, so every technician
///     gives every client the same service. The built-in one is the "Servicio estándar"; others are
///     imported from JSON and validated before Zentra uses them.
/// </summary>
public sealed record ServicePreset
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;
    public required string Name { get; init; }

    /// <summary>The Zentra version that wrote the file. Informative only.</summary>
    public string ZentraVersion { get; init; } = Shared.FileVersion;

    public DateOnly Created { get; init; }

    /// <summary>Risky settings are refused unless the preset says so (and the CLI is told so too).</summary>
    public bool AllowRisky { get; init; }

    /// <summary>Optimization GUIDs, applied in this order.</summary>
    public IReadOnlyList<Guid> Settings { get; init; } = [];

    /// <summary>AppX package names removed when they match exactly.</summary>
    public IReadOnlyList<string> Apps { get; init; } = [];

    /// <summary>Fragments of package names whose publisher prefix varies (OEM games).</summary>
    public IReadOnlyList<string> AppFragments { get; init; } = [];

    /// <summary>True for the profiles built into Zentra, which are not read from a file.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    /// <summary>The text key that says who a built-in profile is for.</summary>
    [JsonIgnore]
    public string? DescriptionKey { get; init; }

    /// <summary>Who a built-in profile is for; empty for an imported one.</summary>
    [JsonIgnore]
    public string Description =>
        DescriptionKey is null ? string.Empty : Services.Configuration.Loc.Instance[DescriptionKey];

    /// <summary>The "Servicio estándar" as a preset: the closed list of <see cref="StandardServiceProfile" />.</summary>
    public static ServicePreset Standard { get; } =
        new()
        {
            Name = "Servicio estándar",
            Created = new DateOnly(2026, 11, 19),
            Settings = StandardServiceProfile.Settings.Select(IdOf).ToList(),
            Apps = StandardServiceProfile.Apps,
            AppFragments = StandardServiceProfile.ThirdPartyGames,
            IsBuiltIn = true,
            DescriptionKey = "Profile.Builtin.Standard",
        };

    /// <summary>The standard profile plus less background work (version 1.1.0).</summary>
    public static ServicePreset SlowPc { get; } =
        BuiltIn("Equipo lento", ServiceProfiles.SlowPcExtra, "Profile.Builtin.SlowPc");

    /// <summary>The standard profile plus graphics, USB and network for games (version 1.1.0).</summary>
    public static ServicePreset Gaming { get; } =
        BuiltIn("Juegos", ServiceProfiles.GamingExtra, "Profile.Builtin.Gaming");

    /// <summary>The standard profile plus telemetry, Copilot, Recall, Edge and WPBT (version 1.1.0).</summary>
    public static ServicePreset Privacy { get; } =
        BuiltIn("Privacidad máxima", ServiceProfiles.PrivacyExtra, "Profile.Builtin.Privacy");

    /// <summary>Every profile built into Zentra, in the order the selector shows them.</summary>
    public static IReadOnlyList<ServicePreset> BuiltIns { get; } = [Standard, SlowPc, Gaming, Privacy];

    private static ServicePreset BuiltIn(string name, IReadOnlyList<Type> extra, string descriptionKey) =>
        Standard with
        {
            Name = name,
            Created = new DateOnly(2026, 11, 20),
            Settings = Standard.Settings.Concat(extra.Select(IdOf)).Distinct().ToList(),
            DescriptionKey = descriptionKey,
        };

    /// <summary>Whether an installed package is one this preset removes. Protected apps never are.</summary>
    public bool RemovesApp(string packageName) =>
        !Shared.IsProtectedApp(packageName)
        && (
            Apps.Any(a => string.Equals(packageName, a, StringComparison.OrdinalIgnoreCase))
            || AppFragments.Any(f => packageName.Contains(f, StringComparison.OrdinalIgnoreCase))
        );

    internal static Guid IdOf(Type optimizationType) =>
        Guid.Parse(
            optimizationType.GetCustomAttribute<OptimizationAttribute>()?.Id
                ?? throw new InvalidOperationException(
                    $"{optimizationType.Name} has no [Optimization] attribute"
                )
        );
}

/// <summary>Why a preset file was refused. The page and the CLI turn these into text.</summary>
public enum PresetProblemKind
{
    NotJson,
    TooLarge,
    UnsupportedSchema,
    MissingName,
    TooManyEntries,
    UnknownSetting,
    DuplicateSetting,
    RiskyNotAllowed,

    /// <summary>Plan F1-09: what cannot be undone is applied one by one, after its own dialog.</summary>
    NeedsConfirmation,
    InvalidAppName,
    ProtectedApp,
    DuplicateApp,
    Empty,
}

public sealed record PresetProblem(PresetProblemKind Kind, string? Detail = null);

public sealed record PresetLoadResult(ServicePreset? Preset, IReadOnlyList<PresetProblem> Problems)
{
    public bool IsValid => Preset is not null && Problems.Count == 0;
}

/// <summary>Reads, validates and writes preset files (schema 1).</summary>
public static partial class ServicePresetFile
{
    /// <summary>A preset is a short list; anything bigger is not one.</summary>
    public const int MaxBytes = 256 * 1024;

    public const int MaxEntries = 300;

    /// <summary>A fragment matches any package that contains it: a short one would match too many.</summary>
    public const int MinFragmentLength = 6;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Strict on purpose: a misspelled field is an error, not a silently ignored setting.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>AppX package names: letters, digits, dots and dashes.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{1,99}$")]
    private static partial Regex PackageName();

    public static string Serialize(ServicePreset preset) =>
        JsonSerializer.Serialize(preset with { ZentraVersion = Shared.FileVersion }, Options);

    public static PresetLoadResult Load(string path, IReadOnlyList<IOptimization> catalog)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxBytes)
            return Fail(PresetProblemKind.TooLarge);
        return Parse(File.ReadAllText(path), catalog);
    }

    /// <summary>
    ///     Parses and validates a preset against the optimizations this Zentra has. Every problem is
    ///     reported, not only the first one, so the technician can fix the file in one go.
    /// </summary>
    public static PresetLoadResult Parse(string json, IReadOnlyList<IOptimization> catalog)
    {
        if (json.Length > MaxBytes)
            return Fail(PresetProblemKind.TooLarge);

        ServicePreset? preset;
        try
        {
            preset = JsonSerializer.Deserialize<ServicePreset>(json, Options);
        }
        catch (JsonException ex)
        {
            return Fail(PresetProblemKind.NotJson, ex.Message);
        }
        if (preset is null)
            return Fail(PresetProblemKind.NotJson);

        var problems = new List<PresetProblem>();
        if (preset.Schema != ServicePreset.CurrentSchema)
            return Fail(PresetProblemKind.UnsupportedSchema, preset.Schema.ToString());
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80)
            problems.Add(new(PresetProblemKind.MissingName));

        var settings = preset.Settings ?? [];
        var apps = preset.Apps ?? [];
        var fragments = preset.AppFragments ?? [];
        if (settings.Count + apps.Count + fragments.Count == 0)
            problems.Add(new(PresetProblemKind.Empty));
        if (settings.Count > MaxEntries || apps.Count + fragments.Count > MaxEntries)
            return Fail(PresetProblemKind.TooManyEntries);

        var byId = catalog.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<Guid>();
        foreach (var id in settings)
        {
            if (!seen.Add(id))
            {
                problems.Add(new(PresetProblemKind.DuplicateSetting, id.ToString()));
                continue;
            }
            if (!byId.TryGetValue(id, out var optimization))
            {
                problems.Add(new(PresetProblemKind.UnknownSetting, id.ToString()));
                continue;
            }
            if (optimization.Confirmation != OptimizationConfirmation.None)
                problems.Add(new(PresetProblemKind.NeedsConfirmation, optimization.Name));
            else if (optimization.Risk == OptimizationRisk.Risky && !preset.AllowRisky)
                problems.Add(new(PresetProblemKind.RiskyNotAllowed, optimization.Name));
        }

        var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps.Concat(fragments))
        {
            if (
                app is null
                || !PackageName().IsMatch(app)
                || (fragments.Contains(app) && app.Length < MinFragmentLength)
            )
                problems.Add(new(PresetProblemKind.InvalidAppName, app));
            else if (Shared.IsProtectedApp(app))
                problems.Add(new(PresetProblemKind.ProtectedApp, app));
            else if (!seenApps.Add(app))
                problems.Add(new(PresetProblemKind.DuplicateApp, app));
        }

        return new PresetLoadResult(
            preset with
            {
                Settings = settings,
                Apps = apps,
                AppFragments = fragments,
                IsBuiltIn = false,
            },
            problems
        );
    }

    private static PresetLoadResult Fail(PresetProblemKind kind, string? detail = null) =>
        new(null, [new PresetProblem(kind, detail)]);
}
