using optimizerDuck.Services.System;

namespace optimizerDuck.Domain.Diagnostics;

/// <summary>Snapshot sections. Each one fails on its own without hiding the others.</summary>
public enum DiagnosticSection
{
    Os,
    Cpu,
    Board,
    Ram,
    Gpu,
    Disks,
    SystemVolume,
    Config,
    Stability,
}

/// <summary>How a section's collection went.</summary>
public enum SectionStatus
{
    Ok,

    /// <summary>Some values were read, some were not.</summary>
    Partial,
    TimedOut,
    Failed,
}

/// <summary>Physical disk health as Windows reports it (MSFT_PhysicalDisk.HealthStatus).</summary>
public enum DiskHealth
{
    Healthy,
    Warning,
    Unhealthy,
}

/// <summary>The active power scheme, by its well-known GUID. Custom schemes are Other.</summary>
public enum PowerPlanKind
{
    Balanced,
    HighPerformance,
    UltimatePerformance,
    PowerSaver,
    Other,
}

/// <summary>TCP receive window auto-tuning level (MSFT_NetTCPSetting.AutoTuningLevelLocal).</summary>
public enum TcpAutoTuning
{
    Disabled,
    HighlyRestricted,
    Restricted,
    Normal,
    Experimental,
}

/// <summary>
///     Everything the rules read, collected once. It is anonymous by construction: no serial
///     numbers, volume labels, device instance paths, file paths, app names, computer or user
///     names, IP or MAC addresses. Hardware text is trimmed and capped.
/// </summary>
public sealed record DiagnosticSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>When the scan ran. Rules use this as "now".</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    public IReadOnlyDictionary<DiagnosticSection, SectionStatus> Sections { get; init; } =
        new Dictionary<DiagnosticSection, SectionStatus>();

    public required OsSnapshot Os { get; init; }
    public required CpuSnapshot Cpu { get; init; }
    public required BoardSnapshot Board { get; init; }
    public required RamSnapshot Ram { get; init; }
    public required IReadOnlyList<GpuSnapshot> Gpus { get; init; }
    public required IReadOnlyList<DiskSnapshot> Disks { get; init; }
    public required SystemVolumeSnapshot SystemVolume { get; init; }
    public required ConfigSnapshot Config { get; init; }
    public required StabilitySnapshot Stability { get; init; }

    /// <summary>
    ///     True when every GPU is integrated (an APU or iGPU drives the screen and shares RAM).
    ///     Not measured when no GPU was read or one could not be classified.
    /// </summary>
    public Measured<bool> IntegratedGraphicsOnly { get; init; }
}

public sealed record OsSnapshot
{
    public Measured<string> Edition { get; init; }
    public Measured<int> Build { get; init; }
    public Measured<string> DisplayVersion { get; init; }
    public Measured<DeviceKind> DeviceKind { get; init; }
    public Measured<bool> Activated { get; init; }
    public Measured<bool> Vbs { get; init; }
    public Measured<bool> Hvci { get; init; }
    public Measured<bool> SecureBoot { get; init; }
}

public sealed record CpuSnapshot
{
    public Measured<string> Model { get; init; }
    public CpuVendor Vendor { get; init; } = CpuVendor.Unknown;
    public Measured<int> Cores { get; init; }
    public Measured<int> Threads { get; init; }
}

public sealed record BoardSnapshot
{
    public Measured<string> Manufacturer { get; init; }
    public Measured<string> Product { get; init; }

    /// <summary>The SMBIOS BIOS version, e.g. "P10.10".</summary>
    public Measured<string> BiosVersion { get; init; }
    public Measured<DateOnly> BiosDate { get; init; }
}

public sealed record RamSnapshot
{
    /// <summary>Sum of the installed modules. Never the memory Windows sees (UMA takes a share).</summary>
    public Measured<long> InstalledBytes { get; init; }

    /// <summary>Memory Windows can use (after the integrated GPU's reservation).</summary>
    public Measured<long> VisibleBytes { get; init; }
    public MemoryType Type { get; init; } = MemoryType.Unknown;
    public IReadOnlyList<RamModuleSnapshot> Modules { get; init; } = [];
}

public sealed record RamModuleSnapshot
{
    /// <summary>Bank and locator, e.g. "P0 CHANNEL A / DIMM 0".</summary>
    public required string Slot { get; init; }
    public long CapacityBytes { get; init; }

    /// <summary>The speed the module runs at now.</summary>
    public Measured<int> ConfiguredMTps { get; init; }

    /// <summary>Win32_PhysicalMemory.Speed. Some boards report the configured speed here.</summary>
    public Measured<int> ReportedMTps { get; init; }
    public Measured<string> PartNumber { get; init; }
}

public sealed record GpuSnapshot
{
    public Measured<string> Model { get; init; }
    public GpuVendor Vendor { get; init; } = GpuVendor.Unknown;
    public Measured<bool> IsIntegrated { get; init; }

    /// <summary>Dedicated memory; on an APU this is the UMA carve-out set in the BIOS.</summary>
    public Measured<int> DedicatedMB { get; init; }
    public Measured<string> DriverVersion { get; init; }
    public Measured<DateOnly> DriverDate { get; init; }
}

public sealed record DiskSnapshot
{
    /// <summary>Windows disk number. Identifies the disk inside one scan only.</summary>
    public int Number { get; init; }
    public Measured<string> Model { get; init; }
    public StorageMediaType Kind { get; init; } = StorageMediaType.Unknown;
    public Measured<string> Bus { get; init; }
    public Measured<long> SizeBytes { get; init; }
    public bool IsSystem { get; init; }
    public Measured<DiskHealth> Health { get; init; }

    /// <summary>Percentage of rated life used (MSFT_StorageReliabilityCounter.Wear).</summary>
    public Measured<int> WearPercent { get; init; }
    public Measured<long> UncorrectedErrors { get; init; }
    public Measured<int> PowerOnHours { get; init; }
    public Measured<int> TemperatureC { get; init; }

    /// <summary>Total host writes. Needs smartctl, which Zentra does not ship yet.</summary>
    public Measured<long> BytesWritten { get; init; }
}

public sealed record SystemVolumeSnapshot
{
    public Measured<long> TotalBytes { get; init; }
    public Measured<long> FreeBytes { get; init; }
    public StorageMediaType Kind { get; init; } = StorageMediaType.Unknown;
}

public sealed record ConfigSnapshot
{
    public Measured<PowerPlanKind> PowerPlan { get; init; }
    public Measured<TcpAutoTuning> TcpAutoTuning { get; init; }
    public Measured<bool> TrimEnabled { get; init; }
    public Measured<bool> PagefileAutomatic { get; init; }
    public Measured<bool> StorageSenseEnabled { get; init; }

    /// <summary>Enabled startup apps. Only the count: app names never leave the PC.</summary>
    public Measured<int> StartupAppsEnabled { get; init; }
}

public sealed record StabilitySnapshot
{
    /// <summary>Kernel-Power 41 events (unexpected shutdowns) in the last 30 days.</summary>
    public Measured<int> Kp41Count30d { get; init; }

    /// <summary>Kernel-Power 41 events caused by holding the power button (not a crash).</summary>
    public Measured<int> Kp41PowerButton30d { get; init; }

    /// <summary>BugCheck (blue screen) events in the last 30 days.</summary>
    public Measured<int> BugChecks30d { get; init; }

    /// <summary>Minidump files written in the last 30 days.</summary>
    public Measured<int> Minidumps30d { get; init; }
}
