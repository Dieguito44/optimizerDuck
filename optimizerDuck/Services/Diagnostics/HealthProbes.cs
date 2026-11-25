using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.System;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>
///     Read-only probes of the extended diagnosis (version 1.2.0): runtimes, program crashes,
///     the clock, driver dumps, the antivirus and leaks. Each failed read is "not measured"
///     with a reason; nothing is changed.
/// </summary>
internal static class HealthProbes
{
    private const long ThirtyDaysMs = 30L * 24 * 60 * 60 * 1000;
    private const int MaxCrashEvents = 2000;

    private static NotMeasuredReason DeniedOrFailed =>
        Environment.IsPrivilegedProcess
            ? NotMeasuredReason.QueryFailed
            : NotMeasuredReason.AccessDenied;

    // ---- Visual C++ and .NET Framework ------------------------------------------------------

    public static Measured<IReadOnlyList<RuntimeStatus>> ReadRuntimes()
    {
        try
        {
            var list = new List<RuntimeStatus>();
            if (Environment.Is64BitOperatingSystem)
                list.Add(ReadVcRuntime("x64"));
            list.Add(ReadVcRuntime("x86"));
            list.Add(ReadNetFramework());
            return Measured<IReadOnlyList<RuntimeStatus>>.Of(list);
        }
        catch (Exception ex)
            when (ex is global::System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Measured<IReadOnlyList<RuntimeStatus>>.Missing(NotMeasuredReason.AccessDenied);
        }
        catch (IOException)
        {
            return Measured<IReadOnlyList<RuntimeStatus>>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    /// <summary>The installer is 32-bit, so the x64 runtime may be under WOW6432Node too.</summary>
    private static RuntimeStatus ReadVcRuntime(string arch)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(
                $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}"
            );
            if (key?.GetValue("Installed") is not int installed || installed != 1)
                continue;
            var version =
                key.GetValue("Major") is int major
                && key.GetValue("Minor") is int minor
                && key.GetValue("Bld") is int build
                    ? $"{major}.{minor}.{build}"
                    : (key.GetValue("Version") as string)?.TrimStart('v', 'V');
            return new RuntimeStatus($"vcredist-{arch}", true, version);
        }
        return new RuntimeStatus($"vcredist-{arch}", false, null);
    }

    /// <summary>Release 528040 or higher is .NET Framework 4.8 (Windows 10 1903 and later have it).</summary>
    private static RuntimeStatus ReadNetFramework()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"
        );
        var release = key?.GetValue("Release") as int?;
        return release switch
        {
            >= 533320 => new RuntimeStatus("netfx-4.8", true, "4.8.1"),
            >= 528040 => new RuntimeStatus("netfx-4.8", true, "4.8"),
            _ => new RuntimeStatus("netfx-4.8", false, null),
        };
    }

    // ---- Program crashes (Application Error 1000) -------------------------------------------

    public sealed record CrashesResult(
        Measured<int> Total,
        IReadOnlyList<CrashGroup> Groups,
        IReadOnlyList<CrashedApp> Apps
    );

    public static CrashesResult ReadAppCrashes()
    {
        var modules = new List<(string App, string Module)>();
        try
        {
            var query = new EventLogQuery(
                "Application",
                PathType.LogName,
                $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]"
            )
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            for (
                var record = reader.ReadEvent();
                record is not null && modules.Count < MaxCrashEvents;
                record = reader.ReadEvent()
            )
                using (record)
                {
                    // EventData: 0 application, 1 its version, 2 its timestamp, 3 faulting module.
                    var p = record.Properties;
                    modules.Add(
                        (
                            p.Count > 0 ? p[0].Value?.ToString() ?? "" : "",
                            p.Count > 3 ? p[3].Value?.ToString() ?? "" : ""
                        )
                    );
                }
        }
        catch (UnauthorizedAccessException)
        {
            return new(Measured<int>.Missing(NotMeasuredReason.AccessDenied), [], []);
        }
        catch (EventLogException)
        {
            return new(Measured<int>.Missing(NotMeasuredReason.QueryFailed), [], []);
        }
        return Summarize(modules);
    }

    /// <summary>Counts by module kind and the three programs that crashed most (pure, for tests).</summary>
    internal static CrashesResult Summarize(IReadOnlyList<(string App, string Module)> crashes)
    {
        var groups = crashes
            .GroupBy(c => AppCrashesRule.GroupOf(c.Module))
            .Select(g => new CrashGroup(g.Key, g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();
        var apps = crashes
            .Where(c => !string.IsNullOrWhiteSpace(c.App))
            .GroupBy(c => c.App.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CrashedApp(g.Key, g.Count()))
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        return new(Measured<int>.Of(crashes.Count), groups, apps);
    }

    // ---- Clock --------------------------------------------------------------------------------

    private const string TimeServer = "time.windows.com";
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One SNTP request to the server Windows itself uses. Without internet: not measured.</summary>
    public static Measured<double> ReadClockOffset()
    {
        try
        {
            var request = new byte[48];
            request[0] = 0x1B; // version 3, client mode
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 3000;
            udp.Client.SendTimeout = 3000;
            udp.Connect(TimeServer, 123);
            var sent = DateTime.UtcNow;
            udp.Send(request, request.Length);
            IPEndPoint? from = null;
            var reply = udp.Receive(ref from);
            var received = DateTime.UtcNow;
            return SntpOffsetSeconds(reply, sent, received) is { } offset
                ? Measured<double>.Of(offset)
                : Measured<double>.Missing(NotMeasuredReason.NotReported);
        }
        catch (SocketException)
        {
            return Measured<double>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    /// <summary>
    ///     The local clock minus the server's, from an SNTP reply (RFC 4330): positive when the
    ///     PC is ahead. Null for a reply that is not a valid server answer.
    /// </summary>
    internal static double? SntpOffsetSeconds(byte[] reply, DateTime sentUtc, DateTime receivedUtc)
    {
        if (reply.Length < 48 || (reply[0] & 0x07) != 4 || reply[1] == 0)
            return null; // not a server reply, or stratum 0 ("kiss of death")
        var serverReceived = NtpTime(reply, 32);
        var serverSent = NtpTime(reply, 40);
        if (serverReceived is null || serverSent is null)
            return null;
        var serverAhead =
            ((serverReceived.Value - sentUtc) + (serverSent.Value - receivedUtc)).TotalSeconds / 2;
        return Math.Round(-serverAhead, 1);
    }

    private static DateTime? NtpTime(byte[] data, int offset)
    {
        ulong seconds =
            ((ulong)data[offset] << 24)
            | ((ulong)data[offset + 1] << 16)
            | ((ulong)data[offset + 2] << 8)
            | data[offset + 3];
        ulong fraction =
            ((ulong)data[offset + 4] << 24)
            | ((ulong)data[offset + 5] << 16)
            | ((ulong)data[offset + 6] << 8)
            | data[offset + 7];
        if (seconds == 0)
            return null;
        return NtpEpoch.AddMilliseconds(seconds * 1000.0 + fraction * 1000.0 / 0x1_0000_0000);
    }

    // ---- Driver and watchdog dumps -------------------------------------------------------------

    public static string LiveKernelReportsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "LiveKernelReports"
        );

    public static Measured<long> ReadLiveKernelReportsBytes()
    {
        try
        {
            var dir = LiveKernelReportsPath;
            if (!Directory.Exists(dir))
                return Measured<long>.Of(0);
            if (SafeTreeWalk.IsLink(dir))
                return Measured<long>.Missing(NotMeasuredReason.NotApplicable);
            // Only administrators can list it, and the tree walk skips what it cannot read:
            // without rights the total would be a made-up 0 (seen on Diego's PC, 24/11/2026).
            if (!Environment.IsPrivilegedProcess)
                return Measured<long>.Missing(NotMeasuredReason.AccessDenied);
            using (var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator())
                entries.MoveNext(); // throws UnauthorizedAccessException when it is not readable
            long total = 0;
            foreach (var file in SafeTreeWalk.Files(dir, "*", true, () => { }))
                total += file.Length;
            return Measured<long>.Of(total);
        }
        catch (UnauthorizedAccessException)
        {
            return Measured<long>.Missing(NotMeasuredReason.AccessDenied);
        }
        catch (IOException)
        {
            return Measured<long>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    // ---- Antivirus ----------------------------------------------------------------------------

    public static Measured<IReadOnlyList<AntivirusEntry>> ReadAntivirus()
    {
        if (IsServer())
            return Measured<IReadOnlyList<AntivirusEntry>>.Missing(NotMeasuredReason.NotApplicable);

        var rows = WmiHelper.Query(
            "SELECT displayName, productState FROM AntiVirusProduct",
            static items =>
                items
                    .Select(mo =>
                    {
                        var name = WmiHelper.GetString(mo, "displayName")?.Trim() ?? "";
                        var (enabled, upToDate) = AntivirusRule.Decode(
                            (uint)(WmiHelper.GetLong(mo, "productState") ?? 0)
                        );
                        return new AntivirusEntry(
                            name,
                            enabled,
                            upToDate,
                            name.Contains("Defender", StringComparison.OrdinalIgnoreCase)
                        );
                    })
                    .Where(a => a.Name.Length > 0)
                    .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            @"root\SecurityCenter2"
        );
        return rows is null
            ? Measured<IReadOnlyList<AntivirusEntry>>.Missing(DeniedOrFailed)
            : Measured<IReadOnlyList<AntivirusEntry>>.Of(rows);
    }

    private static bool IsServer()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\ProductOptions"
        );
        var type = key?.GetValue("ProductType") as string;
        return type is not null && !type.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
    }

    // ---- Leaks --------------------------------------------------------------------------------

    private const uint QueryLimitedInformation = 0x1000;
    private const uint GdiObjects = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    private static readonly HashSet<string> KernelProcesses = new(
        ["Idle", "System", "Registry", "Memory Compression", "Secure System"],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Only the programs over the leak limits; the names stay on the PC.</summary>
    public static Measured<IReadOnlyList<LeakingProcess>> ReadLeakingProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return Measured<IReadOnlyList<LeakingProcess>>.Missing(NotMeasuredReason.QueryFailed);
        }

        var leaking = new List<LeakingProcess>();
        foreach (var process in processes)
            using (process)
            {
                try
                {
                    if (process.Id is 0 or 4 || KernelProcesses.Contains(process.ProcessName))
                        continue;
                    var handles = process.HandleCount;
                    var gdi = ReadGdiObjects(process.Id);
                    if (LeakRule.IsLeaking(handles, gdi))
                        leaking.Add(new LeakingProcess(process.ProcessName + ".exe", handles, gdi));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Exited meanwhile, or protected: skip it.
                }
            }
        return Measured<IReadOnlyList<LeakingProcess>>.Of(
            leaking.OrderByDescending(p => p.Handles).ToList()
        );
    }

    private static int ReadGdiObjects(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == 0)
            return 0;
        try
        {
            return (int)GetGuiResources(handle, GdiObjects);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
