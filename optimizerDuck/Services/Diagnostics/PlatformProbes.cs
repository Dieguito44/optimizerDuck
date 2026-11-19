using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using optimizerDuck.Domain.Diagnostics;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>One Windows Update install result from the System log.</summary>
public sealed record UpdateEvent(DateTime Time, bool Success, string Title);

/// <summary>What the update history says (plan F2-13).</summary>
public sealed record UpdateHistory(int DaysSinceKbSuccess, int StuckKbUpdates);

/// <summary>Battery report facts (plan F2-12): capacity against design, and charge cycles.</summary>
public sealed record BatteryReport(int? CapacityPercent, int? CycleCount);

/// <summary>
///     Read-only probes of the plan of improvements, block 2B: TPM, Secure Boot certificate,
///     Windows Update health, system protection and the battery report. Each read fails on its
///     own and comes back "not measured", never as an invented value.
/// </summary>
public static partial class PlatformProbes
{
    /// <summary>How far back the update history is read.</summary>
    public const int UpdateWindowDays = 90;

    /// <summary>Services Windows Update needs; any of them disabled stops updates.</summary>
    public static readonly string[] UpdateServices = ["wuauserv", "BITS", "UsoSvc"];

    // ----- TPM (F2-11) -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    private const uint TbsTpmNotFound = 0x8028400F;

    /// <summary>
    ///     The TPM version through the TPM Base Services (no WMI, no elevation): 2 for TPM 2.0,
    ///     1 for TPM 1.2, 0 when the PC has none (or it is off in the firmware).
    /// </summary>
    public static Measured<int> ReadTpmVersion()
    {
        try
        {
            var rc = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info);
            if (rc == TbsTpmNotFound)
                return Measured<int>.Of(0);
            if (rc != 0)
                return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
            return info.TpmVersion switch
            {
                1 => Measured<int>.Of(1),
                2 => Measured<int>.Of(2),
                _ => Measured<int>.Missing(NotMeasuredReason.NotReported),
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Measured<int>.Missing(NotMeasuredReason.NotReported);
        }
    }

    // ----- Secure Boot certificate 2023 (F2-11) ------------------------------------------

    /// <summary>
    ///     Whether the "Windows UEFI CA 2023" certificate is in the Secure Boot database, as
    ///     Windows records it while it rolls the certificates of 2011 out: 0 not yet, 1 in the
    ///     database, 2 in the database and booting with the 2023-signed boot manager.
    /// </summary>
    public static Measured<int> ReadUefiCa2023()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing"
            );
            return key?.GetValue("WindowsUEFICA2023Capable") is int capable
                ? Measured<int>.Of(capable)
                : Measured<int>.Missing(NotMeasuredReason.NotReported);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Measured<int>.Missing(NotMeasuredReason.AccessDenied);
        }
    }

    // ----- Windows Update health (F2-13) -------------------------------------------------

    /// <summary>"Installation successful" (19) and "installation failure" (20) events.</summary>
    public static Measured<UpdateHistory> ReadUpdateHistory(DateTime now)
    {
        try
        {
            var since = now.ToUniversalTime().AddDays(-UpdateWindowDays).ToString("o");
            var query = new EventLogQuery(
                "System",
                PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20) and TimeCreated[@SystemTime>='{since}']]]"
            );
            var events = new List<UpdateEvent>();
            using var reader = new EventLogReader(query);
            while (reader.ReadEvent() is { } e)
                using (e)
                {
                    // 19 has the title in its first field; 20 has the error code first.
                    var titleIndex = e.Id == 20 ? 1 : 0;
                    if (
                        e.TimeCreated is { } time
                        && e.Properties.Count > titleIndex
                        && e.Properties[titleIndex].Value is string title
                    )
                        events.Add(new UpdateEvent(time, e.Id == 19, title));
                }
            return Measured<UpdateHistory>.Of(AnalyzeUpdates(events, now));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return Measured<UpdateHistory>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    [GeneratedRegex(@"\bKB\d{6,8}\b", RegexOptions.IgnoreCase)]
    private static partial Regex KbNumber();

    /// <summary>
    ///     Only Windows updates count: titles with a KB number. Microsoft Store apps (failures
    ///     like 0x80073D02 "the app was open") and Defender's definition updates (KB2267602,
    ///     several a day) say nothing about whether Windows Update works.
    /// </summary>
    internal static bool IsWindowsUpdate(string title) =>
        KbNumber().IsMatch(title)
        && !title.Contains("KB2267602", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Days since the last successful Windows update (the window length when there was none),
    ///     and updates that failed 3 times or more in 30 days without a later success.
    /// </summary>
    internal static UpdateHistory AnalyzeUpdates(IEnumerable<UpdateEvent> events, DateTime now)
    {
        var windows = events.Where(e => IsWindowsUpdate(e.Title)).ToList();
        var lastSuccess = windows.Where(e => e.Success).Select(e => e.Time).DefaultIfEmpty().Max();
        var days =
            lastSuccess == default
                ? UpdateWindowDays
                : Math.Min(UpdateWindowDays, (int)(now - lastSuccess).TotalDays);

        var recent = now.AddDays(-30);
        var stuck = windows
            .Where(e => e.Time >= recent)
            .GroupBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Count(g =>
            {
                var failures = g.Where(e => !e.Success).ToList();
                if (failures.Count < 3)
                    return false;
                var lastFailure = failures.Max(e => e.Time);
                return !g.Any(e => e.Success && e.Time > lastFailure);
            });
        return new UpdateHistory(days, stuck);
    }

    /// <summary>
    ///     Windows Update services set to Disabled (Start = 4), by hand or by another
    ///     "optimizer". A service that is not installed is simply not listed.
    /// </summary>
    public static Measured<IReadOnlyList<string>> ReadDisabledUpdateServices()
    {
        try
        {
            var disabled = new List<string>();
            foreach (var name in UpdateServices)
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{name}"
                );
                if (key?.GetValue("Start") is int start && start == 4)
                    disabled.Add(name);
            }
            return Measured<IReadOnlyList<string>>.Of(disabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Measured<IReadOnlyList<string>>.Missing(NotMeasuredReason.AccessDenied);
        }
    }

    // ----- System protection (F2-14) -----------------------------------------------------

    /// <summary>
    ///     Whether System Restore is on: off when a policy disables it, otherwise what Windows
    ///     records ("RPSessionInterval" 1 on, 0 off).
    /// </summary>
    public static Measured<bool> ReadSystemProtection()
    {
        try
        {
            using (
                var policy = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"
                )
            )
                if (policy?.GetValue("DisableSR") is int disabled && disabled == 1)
                    return Measured<bool>.Of(false);

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"
            );
            return key?.GetValue("RPSessionInterval") switch
            {
                int interval => Measured<bool>.Of(interval != 0),
                _ => Measured<bool>.Missing(NotMeasuredReason.NotReported),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Measured<bool>.Missing(NotMeasuredReason.AccessDenied);
        }
    }

    // ----- Battery report (F2-12) --------------------------------------------------------

    /// <summary>
    ///     "powercfg /batteryreport /xml" into a temporary file that is read and deleted at once.
    ///     Only the capacities and the cycle count are kept: the report also names the PC and
    ///     the battery's serial number, which never leave this method.
    /// </summary>
    public static Measured<BatteryReport> ReadBatteryReport()
    {
        var file = Path.Combine(Path.GetTempPath(), $"zentra-battery-{Guid.NewGuid():N}.xml");
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
                    Arguments = $"/batteryreport /xml /output \"{file}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Environment.SystemDirectory,
                }
            );
            if (process is null)
                return Measured<BatteryReport>.Missing(NotMeasuredReason.QueryFailed);
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException) { }
                return Measured<BatteryReport>.Missing(NotMeasuredReason.TimedOut);
            }
            if (process.ExitCode != 0 || !File.Exists(file))
                return Measured<BatteryReport>.Missing(NotMeasuredReason.QueryFailed);

            return ParseBatteryReport(File.ReadAllText(file)) is { } report
                ? Measured<BatteryReport>.Of(report)
                : Measured<BatteryReport>.Missing(NotMeasuredReason.NotApplicable);
        }
        catch (Exception ex)
            when (ex
                    is global::System.ComponentModel.Win32Exception
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            return Measured<BatteryReport>.Missing(NotMeasuredReason.QueryFailed);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    ///     Battery capacity (WMI first, the battery report when WMI does not say) and charge
    ///     cycles (battery report). On a PC without a battery both are "not applicable" and the
    ///     report is not even run.
    /// </summary>
    public static (Measured<int> Capacity, Measured<int> Cycles) ReadBattery()
    {
        var wmi = MaintenanceProbes.ReadBatteryCapacityPercent();
        if (!wmi.HasValue && wmi.Reason == NotMeasuredReason.NotApplicable)
            return (wmi, Measured<int>.Missing(NotMeasuredReason.NotApplicable));

        var report = ReadBatteryReport();
        if (!report.TryGet(out var r))
            return (wmi, Measured<int>.Missing(report.Reason));

        var capacity =
            wmi.HasValue ? wmi
            : r.CapacityPercent is { } percent ? Measured<int>.Of(percent)
            : wmi;
        var cycles = r.CycleCount is { } count
            ? Measured<int>.Of(count)
            : Measured<int>.Missing(NotMeasuredReason.NotReported);
        return (capacity, cycles);
    }

    /// <summary>
    ///     The capacities and cycles of every battery in the report (summed, worst cycle count),
    ///     or null when the PC has no battery or the XML is not a battery report.
    /// </summary>
    internal static BatteryReport? ParseBatteryReport(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var batteries = doc.Descendants().Where(e => e.Name.LocalName == "Battery").ToList();
            if (batteries.Count == 0)
                return null;

            long design = 0,
                full = 0;
            int? cycles = null;
            foreach (var battery in batteries)
            {
                design += Number(battery, "DesignCapacity") ?? 0;
                full += Number(battery, "FullChargeCapacity") ?? 0;
                if (Number(battery, "CycleCount") is { } c and > 0)
                    cycles = Math.Max(cycles ?? 0, (int)c);
            }
            int? percent = design > 0 && full > 0 ? (int)Math.Round(100.0 * full / design) : null;
            return new BatteryReport(percent, cycles);
        }
        catch (global::System.Xml.XmlException)
        {
            return null;
        }
    }

    private static long? Number(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value is { } text
        && long.TryParse(text, out var value)
            ? value
            : null;
}
