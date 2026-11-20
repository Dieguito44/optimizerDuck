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
    Maintenance,
    Display,
    Thermal,
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

    /// <summary>Median of the last 3 boots in milliseconds (see BootTimeProbe).</summary>
    public Measured<int> BootMedianMs { get; init; }

    /// <summary>Component store state (DISM CheckHealth, read-only and fast).</summary>
    public Measured<Services.Repair.WindowsImageHealth> ImageHealth { get; init; }

    /// <summary>Software upkeep and protection (phase 6B). Defaults to not measured.</summary>
    public MaintenanceSnapshot Maintenance { get; init; } = new();

    /// <summary>Monitors, display adapters and games (phase 6B). Defaults to not measured.</summary>
    public DisplaySnapshot Display { get; init; } = new();

    /// <summary>CPU temperature and throttling (phase 6B). Defaults to not measured.</summary>
    public ThermalSnapshot Thermal { get; init; } = new();

    /// <summary>TPM and the Secure Boot certificate (improvements 2B). Defaults to not measured.</summary>
    public PlatformSnapshot Platform { get; init; } = new();

    /// <summary>Windows Update health (improvements 2B). Defaults to not measured.</summary>
    public UpdateHealthSnapshot Updates { get; init; } = new();
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

/// <summary>Defender's potentially unwanted app blocking (MSFT_MpPreference.PUAProtection).</summary>
public enum PuaMode
{
    Off = 0,
    On = 1,

    /// <summary>Logs potentially unwanted apps but does not block them.</summary>
    Audit = 2,
}

/// <summary>Phase 6B checks of software upkeep and protection.</summary>
public sealed record MaintenanceSnapshot
{
    /// <summary>Programs winget can upgrade (rows of `winget upgrade`). Only the count.</summary>
    public Measured<int> WingetUpgrades { get; init; }

    /// <summary>PUA blocking. Not applicable when Defender is not the active antivirus.</summary>
    public Measured<PuaMode> PuaProtection { get; init; }

    /// <summary>Battery full charge as a percentage of its design capacity (laptops).</summary>
    public Measured<int> BatteryCapacityPercent { get; init; }

    /// <summary>Link speed of the connected wired adapter, Mb/s.</summary>
    public Measured<int> WiredLinkMbps { get; init; }

    /// <summary>Highest speed that adapter supports (from its speed and duplex options), Mb/s.</summary>
    public Measured<int> WiredMaxMbps { get; init; }

    /// <summary>RAM in use at rest, % (only from a session measurement, phase 3).</summary>
    public Measured<double> IdleRamPercent { get; init; }

    /// <summary>Restore points on the PC.</summary>
    public Measured<int> RestorePoints { get; init; }

    /// <summary>OneDrive signed in with a folder on this PC.</summary>
    public Measured<bool> OneDriveConfigured { get; init; }

    /// <summary>System Restore on (improvements 2B, F2-14).</summary>
    public Measured<bool> SystemProtectionOn { get; init; }

    /// <summary>Battery charge cycles from the battery report (improvements 2B, F2-12).</summary>
    public Measured<int> BatteryCycles { get; init; }
}

/// <summary>How a monitor is connected (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY, grouped).</summary>
public enum DisplayConnector
{
    Unknown,
    Internal,
    Hdmi,
    DisplayPort,
    Dvi,
    Vga,
    Other,
    Wireless,
    IndirectWired,
    Virtual,
}

/// <summary>
///     Monitors and display adapters (phase 6B), from the Windows display configuration (CCD),
///     GDI modes and the kernel adapter types (D3DKMT). Anonymous: no device paths, no
///     \.\DISPLAYn names, no EDID serials, no file paths.
/// </summary>
public sealed record DisplaySnapshot
{
    public IReadOnlyList<DisplayAdapterSnapshot> Adapters { get; init; } = [];
    public IReadOnlyList<MonitorSnapshot> Monitors { get; init; } = [];
    public Measured<bool> RemoteDesktopSession { get; init; }
    public Measured<bool> OnBattery { get; init; }

    /// <summary>Whether "power saving" and "high performance" pick different hardware GPUs.</summary>
    public Measured<bool> GpuPreferenceEffective { get; init; }

    /// <summary>Games Windows has seen run (only counts; paths never leave the PC).</summary>
    public Measured<int> GamesDetected { get; init; }

    /// <summary>Of those, the ones with no GPU preference set.</summary>
    public Measured<int> GamesWithoutGpuPreference { get; init; }
}

public sealed record DisplayAdapterSnapshot
{
    public int Index { get; init; }
    public Measured<string> Model { get; init; }
    public Measured<bool> IsIntegrated { get; init; }

    /// <summary>Can drive a screen: display bit and at least one video source.</summary>
    public Measured<bool> DisplayCapable { get; init; }
    public bool IsDetachable { get; init; }
}

public sealed record MonitorSnapshot
{
    public int Index { get; init; }
    public Measured<string> Model { get; init; }
    public DisplayConnector Connector { get; init; }
    public bool HasEdid { get; init; }
    public bool IsCloned { get; init; }

    /// <summary>Index into <see cref="DisplaySnapshot.Adapters" /> of the GPU driving it.</summary>
    public Measured<int> AdapterIndex { get; init; }
    public Measured<int> CurrentWidth { get; init; }
    public Measured<int> CurrentHeight { get; init; }
    public Measured<int> CurrentHz { get; init; }
    public Measured<int> NativeWidth { get; init; }
    public Measured<int> NativeHeight { get; init; }

    /// <summary>Highest refresh at the native resolution and the current colour depth.</summary>
    public Measured<int> MaxHzAtNative { get; init; }

    public bool IsPhysical =>
        Connector
            is DisplayConnector.Internal
                or DisplayConnector.Hdmi
                or DisplayConnector.DisplayPort
                or DisplayConnector.Dvi
                or DisplayConnector.Vga;
}

/// <summary>CPU heat (phase 6B, R-THERMAL).</summary>
public sealed record ThermalSnapshot
{
    /// <summary>Median CPU package temperature, °C. Needs the PawnIO driver.</summary>
    public Measured<double> CpuTemperatureC { get; init; }

    /// <summary>Firmware throttling events (Kernel-Processor-Power 37) in the last 7 days.</summary>
    public Measured<int> FirmwareThrottling7d { get; init; }
}

/// <summary>Platform security (improvements 2B, F2-11). Secure Boot itself is in the OS section.</summary>
public sealed record PlatformSnapshot
{
    /// <summary>2 for TPM 2.0, 1 for TPM 1.2, 0 for none (or off in the firmware).</summary>
    public Measured<int> TpmVersion { get; init; }

    /// <summary>
    ///     "Windows UEFI CA 2023" in the Secure Boot database: 0 not yet, 1 in the database,
    ///     2 in the database and booting with the 2023 boot manager.
    /// </summary>
    public Measured<int> UefiCa2023 { get; init; }
}

/// <summary>Windows Update health (improvements 2B, F2-13). Only updates with a KB number.</summary>
public sealed record UpdateHealthSnapshot
{
    /// <summary>Days since the last successful Windows update; 90 means none in 90 days.</summary>
    public Measured<int> DaysSinceSuccess { get; init; }

    /// <summary>Updates that failed 3 times or more in 30 days without a later success.</summary>
    public Measured<int> StuckUpdates { get; init; }

    /// <summary>Windows Update services set to Disabled (wuauserv, BITS, UsoSvc).</summary>
    public Measured<IReadOnlyList<string>> DisabledServices { get; init; }
}
