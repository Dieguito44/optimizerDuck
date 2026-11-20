using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Services.Cli;

/// <summary>What Zentra.exe returns in CLI mode (plan of improvements F3-02).</summary>
public enum CliExitCode
{
    /// <summary>Everything asked for was done.</summary>
    Ok = 0,

    /// <summary>Unknown or missing arguments. Nothing was changed.</summary>
    BadArguments = 1,

    /// <summary>The preset file is missing or invalid, or has Experto settings without --allow-risky.</summary>
    InvalidPreset = 2,

    /// <summary>Zentra is not running as administrator.</summary>
    NotElevated = 3,

    /// <summary>No restore point: nothing was applied.</summary>
    RestorePointFailed = 4,

    /// <summary>The preset ran, but some settings or apps failed. The report lists them.</summary>
    Partial = 5,

    /// <summary>The technician said no in the confirmation, or the run was cancelled.</summary>
    Cancelled = 6,

    /// <summary>--verify: a report file is missing or changed since Zentra wrote it.</summary>
    VerificationFailed = 7,

    /// <summary>Something unexpected; the log has the details.</summary>
    Failed = 10,
}

/// <summary>
///     The command line, for example
///     <c>Zentra.exe --preset estandar --diagnose --report D:\Clientes\Ana --client "Ana" --silent</c>.
/// </summary>
public sealed record CliOptions
{
    /// <summary>A preset file, or "estandar" for the built-in "Servicio estándar".</summary>
    public string? PresetPath { get; init; }

    /// <summary>Measure and diagnose: before only, or before and after when there is a preset.</summary>
    public bool Diagnose { get; init; }

    /// <summary>A folder to copy session.json, the PDF and the transcript into.</summary>
    public string? ReportDirectory { get; init; }

    public string? Client { get; init; }

    /// <summary>No confirmation window. The preview still goes to the output.</summary>
    public bool Silent { get; init; }

    /// <summary>Experto (Risky) settings run only with this flag and allowRisky in the preset.</summary>
    public bool AllowRisky { get; init; }

    /// <summary>
    ///     Plan F1-09: removing apps cannot be undone, so the CLI removes the preset's apps only
    ///     when told so with this flag. Without it the settings are applied and the apps stay.
    /// </summary>
    public bool RemoveApps { get; init; }

    /// <summary>The apps stay (the default since F1-09; the flag is still accepted).</summary>
    public bool KeepApps { get; init; }

    public bool RemovesApps => RemoveApps && !KeepApps;

    public bool Help { get; init; }

    /// <summary>Also write the software inventory (plan F3-06).</summary>
    public bool Inventory { get; init; }

    /// <summary>A report folder (or its ZENTRA-SHA256.txt) to check, alone (plan F3-04).</summary>
    public string? VerifyPath { get; init; }

    /// <summary>The follow-up check the scheduled task runs (plan F3-05). Alone.</summary>
    public bool FollowUp { get; init; }

    /// <summary>Deletes the follow-up task and its copy of Zentra (plan F3-05). Alone.</summary>
    public bool RemoveFollowUp { get; init; }

    public bool UsesStandardPreset => BuiltInPreset is { } p && ReferenceEquals(p, ServicePreset.Standard);

    /// <summary>
    ///     A profile built into Zentra named instead of a file (version 1.1.0): "estandar",
    ///     "equipo-lento", "juegos" or "privacidad". Null when the argument is a file.
    /// </summary>
    public ServicePreset? BuiltInPreset =>
        PresetPath is { } p && BuiltInNames.TryGetValue(p, out var preset) ? preset : null;

    private static readonly Dictionary<string, ServicePreset> BuiltInNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["estandar"] = ServicePreset.Standard,
        ["estándar"] = ServicePreset.Standard,
        ["standard"] = ServicePreset.Standard,
        ["equipo-lento"] = ServicePreset.SlowPc,
        ["lento"] = ServicePreset.SlowPc,
        ["juegos"] = ServicePreset.Gaming,
        ["gaming"] = ServicePreset.Gaming,
        ["privacidad"] = ServicePreset.Privacy,
        ["privacy"] = ServicePreset.Privacy,
    };

    /// <summary>True when Zentra was started with CLI arguments instead of to show its window.</summary>
    public static bool IsCliInvocation(IReadOnlyList<string> args) =>
        args.Any(a =>
            (a.StartsWith("--", StringComparison.Ordinal) && !WindowSwitches.Contains(a))
            || a is "/?" or "-h"
        );

    /// <summary>
    ///     Switches that open the window, not a command-line run (version 1.1.2: with
    ///     "--equipo-tecnico" Zentra went into command-line mode and never opened).
    /// </summary>
    public static readonly IReadOnlySet<string> WindowSwitches = new HashSet<string>(
        [Ai.ApiKeyStore.TechnicianSwitch, Measurements.ContinueAfterRestart.Switch],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Parses the arguments. On an error, returns null and says why (in English, for the log).</summary>
    public static CliOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var options = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    return new CliOptions { Help = true };
                case "--diagnose":
                    options = options with { Diagnose = true };
                    break;
                case "--silent":
                    options = options with { Silent = true };
                    break;
                case "--allow-risky":
                    options = options with { AllowRisky = true };
                    break;
                case "--keep-apps":
                    options = options with { KeepApps = true };
                    break;
                case "--remove-apps":
                    options = options with { RemoveApps = true };
                    break;
                case "--inventory":
                    options = options with { Inventory = true };
                    break;
                case "--followup":
                    options = options with { FollowUp = true };
                    break;
                case "--remove-followup":
                    options = options with { RemoveFollowUp = true };
                    break;
                case "--preset":
                case "--report":
                case "--client":
                case "--verify":
                    if (
                        i + 1 >= args.Count
                        || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    )
                    {
                        error = $"{arg} needs a value";
                        return null;
                    }
                    var value = args[++i];
                    options = arg.ToLowerInvariant() switch
                    {
                        "--preset" => options with { PresetPath = value },
                        "--report" => options with { ReportDirectory = value },
                        "--verify" => options with { VerifyPath = value },
                        _ => options with { Client = value },
                    };
                    break;
                default:
                    error = $"unknown argument: {arg}";
                    return null;
            }
        }

        if (options.FollowUp || options.RemoveFollowUp)
        {
            if (
                options
                    != new CliOptions
                    {
                        FollowUp = options.FollowUp,
                        RemoveFollowUp = options.RemoveFollowUp,
                    }
                || (options.FollowUp && options.RemoveFollowUp)
            )
            {
                error = "--followup and --remove-followup go alone";
                return null;
            }
            return options;
        }
        if (options.VerifyPath is not null)
        {
            if (options with { VerifyPath = null } != new CliOptions())
            {
                error = "--verify goes alone";
                return null;
            }
            return options;
        }
        if (!options.Diagnose && options.PresetPath is null && !options.Inventory)
        {
            error = "nothing to do: use --diagnose, --preset, --inventory or --verify";
            return null;
        }
        if (
            options.PresetPath is null
            && (options.AllowRisky || options.KeepApps || options.RemoveApps || options.Silent)
        )
        {
            error =
                "--allow-risky, --keep-apps, --remove-apps and --silent only apply with --preset";
            return null;
        }
        if (options.KeepApps && options.RemoveApps)
        {
            error = "--keep-apps and --remove-apps go apart";
            return null;
        }
        return options;
    }
}
