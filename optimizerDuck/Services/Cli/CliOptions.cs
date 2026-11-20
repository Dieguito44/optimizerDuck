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

    /// <summary>Apply the settings, but do not remove the preset's apps.</summary>
    public bool KeepApps { get; init; }

    public bool Help { get; init; }

    /// <summary>Also write the software inventory (plan F3-06).</summary>
    public bool Inventory { get; init; }

    /// <summary>A report folder (or its ZENTRA-SHA256.txt) to check, alone (plan F3-04).</summary>
    public string? VerifyPath { get; init; }

    public bool UsesStandardPreset => PresetPath is { } p && StandardNames.Contains(p);

    private static readonly HashSet<string> StandardNames = new(
        ["estandar", "estándar", "standard"],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>True when Zentra was started with CLI arguments instead of to show its window.</summary>
    public static bool IsCliInvocation(IReadOnlyList<string> args) =>
        args.Any(a => a.StartsWith("--", StringComparison.Ordinal) || a is "/?" or "-h");

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
                case "--inventory":
                    options = options with { Inventory = true };
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
            && (options.AllowRisky || options.KeepApps || options.Silent)
        )
        {
            error = "--allow-risky, --keep-apps and --silent only apply with --preset";
            return null;
        }
        return options;
    }
}
