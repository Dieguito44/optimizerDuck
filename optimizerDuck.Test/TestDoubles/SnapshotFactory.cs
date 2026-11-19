using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;

namespace optimizerDuck.Test.TestDoubles;

/// <summary>
///     Hand-built diagnostic snapshots for rule tests. <see cref="DiegoPc" /> mirrors the real
///     probe of the development PC (Ryzen 5 5600GT, 2x8 GB DDR4 3600, ASRock B450M-HDV R4.0,
///     Crucial P3 + Kingston A400). Tests change one thing with <c>with</c>.
/// </summary>
public static class SnapshotFactory
{
    public const long GiB = 1024L * 1024 * 1024;

    public static readonly DateTimeOffset Now = new(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);

    public static ReferenceTables Refs => ReferenceTableLoader.Embedded;

    public static RamModuleSnapshot Module(
        string slot,
        int configured = 3600,
        int? reported = 3600,
        string? part = "VAM4UX36C188G-DVALWN",
        long capacity = 8 * GiB
    ) =>
        new()
        {
            Slot = slot,
            CapacityBytes = capacity,
            ConfiguredMTps = Measured<int>.Of(configured),
            ReportedMTps = Measured.FromNullable(reported),
            PartNumber = Measured.FromText(part),
        };

    public static DiskSnapshot Disk(
        int number,
        string model,
        StorageMediaType kind,
        int? wear = 5,
        DiskHealth? health = DiskHealth.Healthy,
        long? errors = 0,
        bool isSystem = false,
        NotMeasuredReason wearReason = NotMeasuredReason.AccessDenied
    ) =>
        new()
        {
            Number = number,
            Model = Measured<string>.Of(model),
            Kind = kind,
            Bus = Measured<string>.Of(kind == StorageMediaType.Nvme ? "NVMe" : "SATA"),
            SizeBytes = Measured<long>.Of(500L * 1000 * 1000 * 1000),
            IsSystem = isSystem,
            Health = health is { } h
                ? Measured<DiskHealth>.Of(h)
                : Measured<DiskHealth>.Missing(NotMeasuredReason.NotReported),
            WearPercent = Measured.FromNullable(wear, wearReason),
            UncorrectedErrors = Measured.FromNullable(errors, NotMeasuredReason.AccessDenied),
            BytesWritten = Measured<long>.Missing(NotMeasuredReason.RequiresExternalTool),
        };

    public static GpuSnapshot RadeonIgpu(int dedicatedMb = 512) =>
        new()
        {
            Model = Measured<string>.Of("AMD Radeon(TM) Graphics"),
            Vendor = GpuVendor.Amd,
            IsIntegrated = Measured<bool>.Of(true),
            DedicatedMB = Measured<int>.Of(dedicatedMb),
            DriverVersion = Measured<string>.Of("31.0.21925.1001"),
            DriverDate = Measured<DateOnly>.Of(new DateOnly(2026, 5, 20)),
        };

    public static DiagnosticSnapshot DiegoPc() =>
        new()
        {
            CapturedAtUtc = Now,
            Os = new OsSnapshot
            {
                Edition = Measured<string>.Of("Pro"),
                Build = Measured<int>.Of(26200),
                DisplayVersion = Measured<string>.Of("25H2"),
                DeviceKind = Measured<DeviceKind>.Of(DeviceKind.Desktop),
                Activated = Measured<bool>.Of(true),
                Vbs = Measured<bool>.Of(true),
                Hvci = Measured<bool>.Of(false),
                SecureBoot = Measured<bool>.Of(true),
            },
            Cpu = new CpuSnapshot
            {
                Model = Measured<string>.Of("AMD Ryzen 5 5600GT with Radeon Graphics"),
                Vendor = CpuVendor.Amd,
                Cores = Measured<int>.Of(6),
                Threads = Measured<int>.Of(12),
            },
            Board = new BoardSnapshot
            {
                Manufacturer = Measured<string>.Of("ASRock"),
                Product = Measured<string>.Of("B450M-HDV R4.0"),
                BiosVersion = Measured<string>.Of("P10.10"),
                BiosDate = Measured<DateOnly>.Of(new DateOnly(2024, 1, 24)),
            },
            Ram = new RamSnapshot
            {
                InstalledBytes = Measured<long>.Of(16 * GiB),
                VisibleBytes = Measured<long>.Of((long)(15.4 * GiB)),
                Type = MemoryType.Ddr4,
                Modules = [Module("P0 CHANNEL A / DIMM 0"), Module("P0 CHANNEL B / DIMM 0")],
            },
            Gpus = [RadeonIgpu()],
            IntegratedGraphicsOnly = Measured<bool>.Of(true),
            Disks =
            [
                // Measured elevated on 2026-11-19: the A400 reports Wear 0 on SATA (unknown) and
                // 1 uncorrected read error; the P3 reports Wear 0 (NVMe, real).
                Disk(
                    0,
                    "KINGSTON SA400S37/480GB",
                    StorageMediaType.Ssd,
                    wear: null,
                    errors: 1,
                    wearReason: NotMeasuredReason.RequiresExternalTool
                ),
                Disk(1, "CT500P3PSSD8", StorageMediaType.Nvme, wear: 0, isSystem: true),
            ],
            SystemVolume = new SystemVolumeSnapshot
            {
                TotalBytes = Measured<long>.Of(464 * GiB),
                FreeBytes = Measured<long>.Of(173 * GiB),
                Kind = StorageMediaType.Nvme,
            },
            Config = new ConfigSnapshot
            {
                PowerPlan = Measured<PowerPlanKind>.Of(PowerPlanKind.Balanced),
                TcpAutoTuning = Measured<TcpAutoTuning>.Of(TcpAutoTuning.Normal),
                TrimEnabled = Measured<bool>.Of(true),
                PagefileAutomatic = Measured<bool>.Of(true),
                StorageSenseEnabled = Measured<bool>.Of(true),
                StartupAppsEnabled = Measured<int>.Of(8),
            },
            Stability = new StabilitySnapshot
            {
                Kp41Count30d = Measured<int>.Of(3),
                Kp41PowerButton30d = Measured<int>.Of(0),
                BugChecks30d = Measured<int>.Of(0),
                Minidumps30d = Measured<int>.Of(0),
            },
        };

    /// <summary>A snapshot where nothing could be measured.</summary>
    public static DiagnosticSnapshot Empty() =>
        new()
        {
            CapturedAtUtc = Now,
            Os = new OsSnapshot(),
            Cpu = new CpuSnapshot(),
            Board = new BoardSnapshot(),
            Ram = new RamSnapshot(),
            Gpus = [],
            Disks = [],
            SystemVolume = new SystemVolumeSnapshot(),
            Config = new ConfigSnapshot(),
            Stability = new StabilitySnapshot(),
        };
}
