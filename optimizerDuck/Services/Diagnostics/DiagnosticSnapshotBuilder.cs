using System.Text;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>One physical disk as the storage probe read it. Raw Windows codes, no serials.</summary>
public sealed record DiskProbeRow
{
    public int Number { get; init; }
    public string? Model { get; init; }

    /// <summary>MSFT_PhysicalDisk.MediaType: 3 HDD, 4 SSD, 5 SCM, 0 unspecified.</summary>
    public int? MediaType { get; init; }

    /// <summary>MSFT_PhysicalDisk.BusType: 11 SATA, 17 NVMe, 7 USB, ...</summary>
    public int? BusType { get; init; }
    public long? SizeBytes { get; init; }

    /// <summary>MSFT_PhysicalDisk.HealthStatus: 0 healthy, 1 warning, 2 unhealthy, 5 unknown.</summary>
    public int? HealthStatus { get; init; }

    public int? Wear { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }
    public int? PowerOnHours { get; init; }
    public int? TemperatureC { get; init; }

    /// <summary>Why the reliability counters are missing, when they are.</summary>
    public NotMeasuredReason CountersReason { get; init; } = NotMeasuredReason.NotReported;
}

/// <summary>Everything the probes read outside <see cref="SystemInfo" />.</summary>
public sealed record DiagnosticProbeData
{
    public IReadOnlyList<DiskProbeRow> Disks { get; init; } = [];
    public Measured<int> SystemDiskNumber { get; init; }
    public Measured<long> PhysicallyInstalledBytes { get; init; }
    public Measured<bool> Activated { get; init; }
    public Measured<TcpAutoTuning> TcpAutoTuning { get; init; }
    public Measured<bool> TrimEnabled { get; init; }
    public Measured<bool> PagefileAutomatic { get; init; }
    public Measured<bool> StorageSenseEnabled { get; init; }
    public Measured<int> StartupAppsEnabled { get; init; }
    public Measured<int> Kp41Count30d { get; init; }
    public Measured<int> Kp41PowerButton30d { get; init; }
    public Measured<int> BugChecks30d { get; init; }
    public Measured<int> Minidumps30d { get; init; }

    public IReadOnlyDictionary<DiagnosticSection, SectionStatus> Statuses { get; init; } =
        new Dictionary<DiagnosticSection, SectionStatus>();
}

/// <summary>
///     Turns raw reads into a <see cref="DiagnosticSnapshot" />. Pure: no I/O, no clock. This is
///     the only place where null and Unknown become "not measured", and where hardware text is
///     cleaned so nothing personal or oversized reaches the report.
/// </summary>
public static class DiagnosticSnapshotBuilder
{
    private const int MaxTextLength = 64;

    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static DiagnosticSnapshot Build(
        SystemInfo info,
        DiagnosticProbeData probes,
        DateTimeOffset capturedAtUtc
    )
    {
        var gpus = info.Gpus.Select(g => BuildGpu(g, info.Cpu)).ToList();
        var disks = probes.Disks.Select(d => BuildDisk(d, probes.SystemDiskNumber)).ToList();
        var sections = new Dictionary<DiagnosticSection, SectionStatus>(probes.Statuses)
        {
            [DiagnosticSection.Os] = info.Windows.BuildNumber is null
                ? SectionStatus.Failed
                : SectionStatus.Ok,
            [DiagnosticSection.Cpu] = info.Cpu.Name is null
                ? SectionStatus.Failed
                : SectionStatus.Ok,
            [DiagnosticSection.Board] = info.Firmware.BoardProduct is null
                ? SectionStatus.Failed
                : SectionStatus.Ok,
            [DiagnosticSection.Ram] =
                info.Memory.Modules.Count == 0 ? SectionStatus.Partial : SectionStatus.Ok,
            [DiagnosticSection.Gpu] = gpus.Count == 0 ? SectionStatus.Failed : SectionStatus.Ok,
        };

        return new DiagnosticSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            Sections = sections,
            Os = BuildOs(info, probes),
            Cpu = new CpuSnapshot
            {
                Model = Text(info.Cpu.Name),
                Vendor = info.Cpu.Vendor,
                Cores = Positive(info.Cpu.CoreCount),
                Threads = Positive(info.Cpu.ThreadCount),
            },
            Board = new BoardSnapshot
            {
                Manufacturer = Text(info.Firmware.BoardManufacturer),
                Product = Text(info.Firmware.BoardProduct),
                BiosVersion = Text(info.Firmware.SmbiosVersion),
                BiosDate = Measured.FromNullable(info.Firmware.BiosReleaseDateOnly),
            },
            Ram = BuildRam(info.Memory, probes),
            Gpus = gpus,
            IntegratedGraphicsOnly = IntegratedOnly(gpus),
            Disks = disks,
            SystemVolume = BuildSystemVolume(info.Storage),
            Config = new ConfigSnapshot
            {
                PowerPlan = MapPowerPlan(info.Power.SchemeId),
                TcpAutoTuning = probes.TcpAutoTuning,
                TrimEnabled = probes.TrimEnabled,
                PagefileAutomatic = probes.PagefileAutomatic,
                StorageSenseEnabled = probes.StorageSenseEnabled,
                StartupAppsEnabled = probes.StartupAppsEnabled,
            },
            Stability = new StabilitySnapshot
            {
                Kp41Count30d = probes.Kp41Count30d,
                Kp41PowerButton30d = probes.Kp41PowerButton30d,
                BugChecks30d = probes.BugChecks30d,
                Minidumps30d = probes.Minidumps30d,
            },
        };
    }

    private static OsSnapshot BuildOs(SystemInfo info, DiagnosticProbeData probes)
    {
        var windows = info.Windows;
        return new OsSnapshot
        {
            Edition =
                windows.Edition == WindowsEdition.Unknown
                    ? Text(windows.EditionId)
                    : Measured<string>.Of(windows.Edition.ToString()),
            Build = Measured.FromNullable(windows.BuildNumber),
            DisplayVersion = Text(windows.DisplayVersion),
            DeviceKind =
                windows.DeviceKind == DeviceKind.Unknown
                    ? Measured<DeviceKind>.Missing(NotMeasuredReason.NotReported)
                    : Measured<DeviceKind>.Of(windows.DeviceKind),
            Activated = probes.Activated,
            Vbs = MapVbs(info.Security.VirtualizationBasedSecurity),
            Hvci = MapVbs(info.Security.MemoryIntegrity),
            SecureBoot = info.Security.SecureBoot switch
            {
                SecureBootState.Enabled => Measured<bool>.Of(true),
                SecureBootState.Disabled or SecureBootState.Unavailable => Measured<bool>.Of(false),
                _ => Measured<bool>.Missing(NotMeasuredReason.NotReported),
            },
        };
    }

    private static RamSnapshot BuildRam(MemoryInfo memory, DiagnosticProbeData probes)
    {
        var modules = memory
            .Modules.Select(m => new RamModuleSnapshot
            {
                Slot = Slot(m),
                CapacityBytes = m.CapacityBytes,
                // Only the configured speed counts: the module's own speed is not what the PC
                // runs at, so falling back to it could hide XMP being off (audit Z-27).
                ConfiguredMTps = Measured.FromNullable(m.ConfiguredSpeedMTps),
                ReportedMTps = Measured.FromNullable(m.RatedSpeedMTps),
                PartNumber = Text(m.PartNumber),
            })
            .ToList();

        var installed =
            modules.Count > 0
                ? Measured<long>.Of(modules.Sum(m => m.CapacityBytes))
                : probes.PhysicallyInstalledBytes;

        return new RamSnapshot
        {
            InstalledBytes = installed,
            VisibleBytes =
                memory.TotalBytes > 0
                    ? Measured<long>.Of(memory.TotalBytes)
                    : Measured<long>.Missing(NotMeasuredReason.NotReported),
            Type = memory.Type,
            Modules = modules,
        };
    }

    private static string Slot(MemoryModuleInfo module)
    {
        var bank = Clean(module.BankLabel);
        var locator = Clean(module.Slot);
        return (bank, locator) switch
        {
            (null, null) => "?",
            (null, { } l) => l,
            ({ } b, null) => b,
            ({ } b, { } l) => $"{b} / {l}",
        };
    }

    private static GpuSnapshot BuildGpu(GpuInfo gpu, CpuInfo cpu)
    {
        var integrated = IntegratedGpuClassifier.Classify(
            cpu.Vendor,
            cpu.Name,
            gpu.Vendor,
            gpu.Name
        );
        return new GpuSnapshot
        {
            Model = Text(gpu.Name),
            Vendor = gpu.Vendor,
            IsIntegrated = Measured.FromNullable(integrated),
            DedicatedMB = Measured.FromNullable(gpu.DedicatedVramMB),
            DriverVersion = Text(gpu.DriverVersion),
            DriverDate = gpu.DriverDate is { } date
                ? Measured<DateOnly>.Of(DateOnly.FromDateTime(date))
                : Measured<DateOnly>.Missing(NotMeasuredReason.NotReported),
        };
    }

    /// <summary>True when every GPU is integrated; false as soon as one is dedicated.</summary>
    internal static Measured<bool> IntegratedOnly(IReadOnlyList<GpuSnapshot> gpus)
    {
        if (gpus.Count == 0)
            return Measured<bool>.Missing(NotMeasuredReason.NotReported);
        if (gpus.Any(g => g.IsIntegrated is { HasValue: true, ValueOrDefault: false }))
            return Measured<bool>.Of(false);
        return gpus.All(g => g.IsIntegrated.HasValue)
            ? Measured<bool>.Of(true)
            : Measured<bool>.Missing(NotMeasuredReason.NotReported);
    }

    private static DiskSnapshot BuildDisk(DiskProbeRow row, Measured<int> systemDisk)
    {
        var reason = row.CountersReason;
        Measured<long> errors =
            row.ReadErrorsUncorrected is null && row.WriteErrorsUncorrected is null
                ? Measured<long>.Missing(reason)
                : Measured<long>.Of(
                    (row.ReadErrorsUncorrected ?? 0) + (row.WriteErrorsUncorrected ?? 0)
                );

        var kind = MapMediaType(row.MediaType, row.BusType);

        // NVMe drives report "Percentage Used" from their health log, so Windows' Wear is real.
        // On SATA, Windows does not decode the vendor SMART attributes and reports 0 even on a
        // worn drive: a 0 there means "unknown", not "new". Needs smartctl.
        var wear = row.Wear switch
        {
            null => Measured<int>.Missing(reason),
            0 when kind != StorageMediaType.Nvme => Measured<int>.Missing(
                NotMeasuredReason.RequiresExternalTool
            ),
            { } value => Measured<int>.Of(value),
        };

        return new DiskSnapshot
        {
            Number = row.Number,
            Model = Text(row.Model),
            Kind = kind,
            Bus = MapBus(row.BusType),
            SizeBytes = Measured.FromNullable(row.SizeBytes),
            IsSystem = systemDisk.TryGet(out var system) && system == row.Number,
            Health = row.HealthStatus switch
            {
                0 => Measured<DiskHealth>.Of(DiskHealth.Healthy),
                1 => Measured<DiskHealth>.Of(DiskHealth.Warning),
                2 => Measured<DiskHealth>.Of(DiskHealth.Unhealthy),
                _ => Measured<DiskHealth>.Missing(NotMeasuredReason.NotReported),
            },
            WearPercent = wear,
            UncorrectedErrors = errors,
            PowerOnHours = Measured.FromNullable(row.PowerOnHours, reason),
            TemperatureC = row.TemperatureC is > 0
                ? Measured<int>.Of(row.TemperatureC.Value)
                : Measured<int>.Missing(reason),
            BytesWritten = Measured<long>.Missing(NotMeasuredReason.RequiresExternalTool),
        };
    }

    internal static StorageMediaType MapMediaType(int? mediaType, int? busType) =>
        mediaType switch
        {
            3 => StorageMediaType.Hdd,
            4 or 5 => busType == 17 ? StorageMediaType.Nvme : StorageMediaType.Ssd,
            _ => busType == 17 ? StorageMediaType.Nvme : StorageMediaType.Unknown,
        };

    internal static Measured<string> MapBus(int? busType) =>
        busType switch
        {
            null => Measured<string>.Missing(NotMeasuredReason.NotReported),
            1 => Measured<string>.Of("SCSI"),
            3 => Measured<string>.Of("ATA"),
            7 => Measured<string>.Of("USB"),
            8 => Measured<string>.Of("RAID"),
            10 => Measured<string>.Of("SAS"),
            11 => Measured<string>.Of("SATA"),
            12 => Measured<string>.Of("SD"),
            17 => Measured<string>.Of("NVMe"),
            _ => Measured<string>.Of("Other"),
        };

    private static SystemVolumeSnapshot BuildSystemVolume(StorageInfo storage)
    {
        var volume = storage.Volumes.FirstOrDefault(v => v.IsSystemDrive);
        if (volume is null || volume.TotalBytes <= 0)
            return new SystemVolumeSnapshot
            {
                TotalBytes = Measured<long>.Missing(NotMeasuredReason.NotReported),
                FreeBytes = Measured<long>.Missing(NotMeasuredReason.NotReported),
            };

        return new SystemVolumeSnapshot
        {
            TotalBytes = Measured<long>.Of(volume.TotalBytes),
            FreeBytes = Measured<long>.Of(volume.FreeBytes),
            Kind = volume.MediaType,
        };
    }

    internal static Measured<PowerPlanKind> MapPowerPlan(Guid? scheme)
    {
        if (scheme is not { } id)
            return Measured<PowerPlanKind>.Missing(NotMeasuredReason.NotReported);
        if (id == Balanced)
            return Measured<PowerPlanKind>.Of(PowerPlanKind.Balanced);
        if (id == HighPerformance)
            return Measured<PowerPlanKind>.Of(PowerPlanKind.HighPerformance);
        if (id == PowerSaver)
            return Measured<PowerPlanKind>.Of(PowerPlanKind.PowerSaver);
        return id == Ultimate
            ? Measured<PowerPlanKind>.Of(PowerPlanKind.UltimatePerformance)
            : Measured<PowerPlanKind>.Of(PowerPlanKind.Other);
    }

    private static Measured<bool> MapVbs(VbsState state) =>
        state switch
        {
            VbsState.Enabled => Measured<bool>.Of(true),
            VbsState.Disabled or VbsState.Unavailable => Measured<bool>.Of(false),
            _ => Measured<bool>.Missing(NotMeasuredReason.NotReported),
        };

    private static Measured<int> Positive(int value) =>
        value > 0 ? Measured<int>.Of(value) : Measured<int>.Missing(NotMeasuredReason.NotReported);

    private static Measured<string> Text(string? value) => Measured.FromText(Clean(value));

    /// <summary>Trims, drops control characters, collapses spaces and caps the length.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value.Trim())
        {
            var isSpace = char.IsWhiteSpace(c); // tabs and newlines become one space
            if (!isSpace && char.IsControl(c))
                continue;
            if (isSpace && lastWasSpace)
                continue;
            builder.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
        }

        var text = builder.ToString().Trim();
        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength].TrimEnd();
        return text.Length == 0 ? null : text;
    }
}
