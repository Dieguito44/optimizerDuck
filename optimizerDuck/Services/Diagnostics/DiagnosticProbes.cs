using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>
///     Read-only probes for what <see cref="SystemInfoService" /> does not collect. Each one maps
///     a failed read to "not measured" with a reason; none of them changes the system.
/// </summary>
internal static partial class DiagnosticProbes
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const string WindowsLicenseAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";
    private const long ThirtyDaysMs = 30L * 24 * 60 * 60 * 1000;

    private static NotMeasuredReason DeniedOrFailed =>
        Environment.IsPrivilegedProcess
            ? NotMeasuredReason.QueryFailed
            : NotMeasuredReason.AccessDenied;

    // ---- Storage ----------------------------------------------------------------------------

    public sealed record StorageProbeResult(
        IReadOnlyList<DiskProbeRow> Disks,
        Measured<int> SystemDiskNumber,
        SectionStatus Status
    );

    public static StorageProbeResult ReadStorage()
    {
        // ObjectId is the class key: without it the objects have no path and the reliability
        // counter (reachable only through the association, never by enumerating its class)
        // cannot be read.
        var rows = WmiHelper.Query(
            "SELECT ObjectId, DeviceId, FriendlyName, MediaType, BusType, SpindleSpeed, Size, HealthStatus FROM MSFT_PhysicalDisk",
            static items =>
                items
                    .Select(mo =>
                    {
                        var row = new DiskProbeRow
                        {
                            Number = int.TryParse(WmiHelper.GetString(mo, "DeviceId"), out var n)
                                ? n
                                : -1,
                            Model = WmiHelper.GetString(mo, "FriendlyName"),
                            MediaType = WmiHelper.GetInt(mo, "MediaType"),
                            BusType = WmiHelper.GetInt(mo, "BusType"),
                            SpindleSpeed = WmiHelper.GetLong(mo, "SpindleSpeed"),
                            SizeBytes = WmiHelper.GetLong(mo, "Size"),
                            HealthStatus = WmiHelper.GetInt(mo, "HealthStatus"),
                        };
                        return WithCounters(mo, row);
                    })
                    .Where(r => r.Number >= 0)
                    .OrderBy(r => r.Number)
                    .ToList(),
            StorageNamespace
        );

        if (rows is null)
            return new StorageProbeResult(
                [],
                Measured<int>.Missing(DeniedOrFailed),
                SectionStatus.Failed
            );

        var allCounters = rows.All(r => r.CountersReason == NotMeasuredReason.NotReported);
        return new StorageProbeResult(
            rows,
            ReadSystemDiskNumber(),
            allCounters ? SectionStatus.Ok : SectionStatus.Partial
        );
    }

    /// <summary>Reads the disk's reliability counter through MSFT_PhysicalDiskToStorageReliabilityCounter.</summary>
    private static DiskProbeRow WithCounters(ManagementObject disk, DiskProbeRow row)
    {
        try
        {
            using var related = disk.GetRelated("MSFT_StorageReliabilityCounter");
            foreach (var item in related)
            {
                using var counter = (ManagementObject)item;
                return row with
                {
                    Wear = WmiHelper.GetInt(counter, "Wear"),
                    TemperatureC = WmiHelper.GetInt(counter, "Temperature"),
                    PowerOnHours = WmiHelper.GetInt(counter, "PowerOnHours"),
                    ReadErrorsUncorrected = WmiHelper.GetLong(counter, "ReadErrorsUncorrected"),
                    WriteErrorsUncorrected = WmiHelper.GetLong(counter, "WriteErrorsUncorrected"),
                    CountersReason = NotMeasuredReason.NotReported,
                };
            }
            return row with { CountersReason = NotMeasuredReason.NotReported };
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return row with { CountersReason = NotMeasuredReason.AccessDenied };
        }
        catch (UnauthorizedAccessException)
        {
            return row with { CountersReason = NotMeasuredReason.AccessDenied };
        }
        catch (ManagementException)
        {
            return row with { CountersReason = DeniedOrFailed };
        }
    }

    private static Measured<int> ReadSystemDiskNumber()
    {
        var letter = Environment.GetEnvironmentVariable("SystemDrive")?.TrimEnd(':', '\\');
        if (string.IsNullOrEmpty(letter))
            return Measured<int>.Missing(NotMeasuredReason.NotReported);

        var number = WmiHelper.Query(
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition",
            items =>
                items
                    .Where(mo =>
                        string.Equals(
                            WmiHelper.GetString(mo, "DriveLetter"),
                            letter,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .Select(mo => WmiHelper.GetInt(mo, "DiskNumber"))
                    .FirstOrDefault(),
            StorageNamespace
        );
        return Measured.FromNullable(number);
    }

    // ---- Stability --------------------------------------------------------------------------

    public sealed record StabilityProbeResult(
        Measured<int> Kp41,
        Measured<int> Kp41PowerButton,
        Measured<int> BugChecks,
        Measured<int> Minidumps,
        SectionStatus Status
    );

    public static StabilityProbeResult ReadStability(DateTimeOffset now)
    {
        var kp41 = CountEvents(
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]",
            static record => IsPowerButton(record.ToXml())
        );
        var bugChecks = CountEvents(
            $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001) and TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]",
            null
        );
        var minidumps = CountMinidumps(now);

        var status =
            kp41.Total.HasValue && bugChecks.Total.HasValue && minidumps.HasValue
                ? SectionStatus.Ok
                : SectionStatus.Partial;
        return new StabilityProbeResult(
            kp41.Total,
            kp41.Flagged,
            bugChecks.Total,
            minidumps,
            status
        );
    }

    private static (Measured<int> Total, Measured<int> Flagged) CountEvents(
        string xpath,
        Func<EventRecord, bool>? flag
    )
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xpath);
            using var reader = new EventLogReader(query);
            int total = 0,
                flagged = 0;
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    total++;
                    if (flag?.Invoke(record) == true)
                        flagged++;
                }
            }
            return (Measured<int>.Of(total), Measured<int>.Of(flagged));
        }
        catch (UnauthorizedAccessException)
        {
            return (
                Measured<int>.Missing(NotMeasuredReason.AccessDenied),
                Measured<int>.Missing(NotMeasuredReason.AccessDenied)
            );
        }
        catch (EventLogException)
        {
            return (
                Measured<int>.Missing(NotMeasuredReason.QueryFailed),
                Measured<int>.Missing(NotMeasuredReason.QueryFailed)
            );
        }
    }

    /// <summary>A Kernel-Power 41 caused by holding the power button is not a crash.</summary>
    internal static bool IsPowerButton(string eventXml)
    {
        var timestamp = PowerButtonTimestamp().Match(eventXml);
        if (timestamp.Success && timestamp.Groups[1].Value != "0")
            return true;
        return LongPress().IsMatch(eventXml);
    }

    [GeneratedRegex(@"Name=['""]PowerButtonTimestamp['""]>\s*(\d+)\s*<")]
    private static partial Regex PowerButtonTimestamp();

    [GeneratedRegex(
        @"Name=['""]LongPowerButtonPressDetected['""]>\s*true\s*<",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex LongPress();

    private static Measured<int> CountMinidumps(DateTimeOffset now)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Minidump"
            );
            if (!Directory.Exists(dir))
                return Measured<int>.Of(0);

            var since = now.UtcDateTime.AddDays(-30);
            return Measured<int>.Of(
                new DirectoryInfo(dir)
                    .EnumerateFiles("*.dmp")
                    .Count(f => f.LastWriteTimeUtc >= since)
            );
        }
        catch (UnauthorizedAccessException)
        {
            return Measured<int>.Missing(NotMeasuredReason.AccessDenied);
        }
        catch (IOException)
        {
            return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    // ---- Configuration ----------------------------------------------------------------------

    public sealed record ConfigProbeResult(
        Measured<TcpAutoTuning> Tcp,
        Measured<bool> Trim,
        Measured<bool> PagefileAutomatic,
        Measured<bool> StorageSense,
        SectionStatus Status
    );

    public static ConfigProbeResult ReadConfig()
    {
        var tcpLevel = WmiHelper.QueryFirst<int?>(
            "SELECT AutoTuningLevelLocal FROM MSFT_NetTCPSetting WHERE SettingName='Internet'",
            static mo => WmiHelper.GetInt(mo, "AutoTuningLevelLocal"),
            @"root\StandardCimv2"
        );
        var tcp = tcpLevel switch
        {
            0 => Measured<TcpAutoTuning>.Of(TcpAutoTuning.Disabled),
            1 => Measured<TcpAutoTuning>.Of(TcpAutoTuning.HighlyRestricted),
            2 => Measured<TcpAutoTuning>.Of(TcpAutoTuning.Restricted),
            3 => Measured<TcpAutoTuning>.Of(TcpAutoTuning.Normal),
            4 => Measured<TcpAutoTuning>.Of(TcpAutoTuning.Experimental),
            _ => Measured<TcpAutoTuning>.Missing(NotMeasuredReason.NotReported),
        };

        // DisableDeleteNotification = 0 means TRIM is sent to the drive.
        var trim = ReadDword(
            @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem",
            "DisableDeleteNotification"
        ) switch
        {
            { HasValue: true, ValueOrDefault: 0 } => Measured<bool>.Of(true),
            { HasValue: true } => Measured<bool>.Of(false),
            var missing => Measured<bool>.Missing(missing.Reason),
        };

        var pagefile = Measured.FromNullable(
            WmiHelper.QueryFirst<bool?>(
                "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem",
                static mo => WmiHelper.GetBool(mo, "AutomaticManagedPagefile")
            )
        );

        // Per user: read from the profile the app runs under (the technician's elevated user).
        var storageSense = ReadDword(
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy",
            "01"
        ) switch
        {
            { HasValue: true, ValueOrDefault: 1 } => Measured<bool>.Of(true),
            { HasValue: true } => Measured<bool>.Of(false),
            var missing => Measured<bool>.Missing(missing.Reason),
        };

        var all = tcp.HasValue && trim.HasValue && pagefile.HasValue && storageSense.HasValue;
        return new ConfigProbeResult(
            tcp,
            trim,
            pagefile,
            storageSense,
            all ? SectionStatus.Ok : SectionStatus.Partial
        );
    }

    private static Measured<int> ReadDword(string path, string name)
    {
        if (!RegistryService.TryReadValue(new RegistryItem(path, name), out var value))
            return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
        return value switch
        {
            int i => Measured<int>.Of(i),
            null => Measured<int>.Missing(NotMeasuredReason.NotReported),
            _ => int.TryParse(value.ToString(), out var parsed)
                ? Measured<int>.Of(parsed)
                : Measured<int>.Missing(NotMeasuredReason.NotReported),
        };
    }

    // ---- Activation and memory --------------------------------------------------------------

    public static Measured<bool> ReadActivation()
    {
        var statuses = WmiHelper.Query(
            $"SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='{WindowsLicenseAppId}' AND PartialProductKey IS NOT NULL",
            static items => items.Select(mo => WmiHelper.GetInt(mo, "LicenseStatus")).ToList()
        );
        if (statuses is null)
            return Measured<bool>.Missing(NotMeasuredReason.QueryFailed);
        if (statuses.Count == 0)
            return Measured<bool>.Missing(NotMeasuredReason.NotReported);
        return Measured<bool>.Of(statuses.Any(s => s == 1)); // 1 = Licensed
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    public static Measured<long> ReadPhysicallyInstalledBytes() =>
        GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0
            ? Measured<long>.Of((long)kb * 1024)
            : Measured<long>.Missing(NotMeasuredReason.NotReported);
}
