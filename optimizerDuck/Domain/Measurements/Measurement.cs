using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using optimizerDuck.Domain.Diagnostics;

namespace optimizerDuck.Domain.Measurements;

/// <summary>When in the service a measurement was taken.</summary>
public enum MeasurementPhase
{
    Before,
    After,
}

/// <summary>Where the boot time came from.</summary>
public enum BootTimeSource
{
    /// <summary>Event 100 of Diagnostics-Performance (full boot, as Windows measures it).</summary>
    DiagnosticsPerformance,

    /// <summary>Plan B: from the kernel start (Kernel-General 12) to the user logon (Winlogon 7001).</summary>
    KernelToLogon,
}

/// <summary>Something Windows blamed for a slow boot (events 101 to 110). Local report only.</summary>
public sealed record BootCulprit(string Name, int EventId, int DelayMs);

/// <summary>
///     One "before" or "after" measurement (plan phase 3). Every value is measured or carries
///     the reason it was not; nothing is estimated.
/// </summary>
public sealed record Measurement
{
    [JsonConverter(typeof(StringEnumConverter))]
    public MeasurementPhase Phase { get; init; }

    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Fast Startup on or off. Boot times are only comparable in the same mode.</summary>
    public Measured<bool> FastStartupEnabled { get; init; }

    /// <summary>Median of the last 3 boots, in milliseconds.</summary>
    public Measured<int> BootMedianMs { get; init; }

    public Measured<BootTimeSource> BootSource { get; init; }

    /// <summary>Up to the desktop, median ms (version 1.2.0). Only when Windows logs event 100.</summary>
    public Measured<int> BootDesktopMs { get; init; }

    /// <summary>The desktop loading startup programs, median ms (version 1.2.0).</summary>
    public Measured<int> BootAfterDesktopMs { get; init; }

    public int BootSamples { get; init; }

    public IReadOnlyList<BootCulprit> BootCulprits { get; init; } = [];

    /// <summary>RAM in use at rest: 60 s average taken at least 5 minutes after logon.</summary>
    public Measured<long> IdleRamUsedBytes { get; init; }

    public Measured<double> IdleRamPercent { get; init; }

    /// <summary>CPU in use over the same 60 s at rest, % (version 1.2.0).</summary>
    public Measured<double> IdleCpuPercent { get; init; }

    public Measured<long> SystemFreeBytes { get; init; }

    public Measured<int> StartupAppsEnabled { get; init; }

    public Measured<int> ProcessCount { get; init; }

    public Measured<int> RunningServices { get; init; }

    /// <summary>Sequential read, 1 MiB blocks (DiskSpd), in MiB/s.</summary>
    public Measured<double> SequentialReadMiBps { get; init; }

    /// <summary>Random read, 4 KiB blocks (DiskSpd), in I/O per second.</summary>
    public Measured<double> RandomReadIops { get; init; }

    /// <summary>
    ///     What Windows was installing while this was measured ("Windows Update", "Microsoft
    ///     Store"); empty when nothing was seen (version 1.1.2).
    /// </summary>
    public IReadOnlyList<string> BackgroundWork { get; init; } = [];
}
