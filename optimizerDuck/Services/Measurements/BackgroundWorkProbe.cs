using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace optimizerDuck.Services.Measurements;

/// <summary>
///     What Windows was installing while Zentra measured (version 1.1.2). In the Hyper-V test the
///     Store installed Teams and Office between "antes" and "después", and the RAM and startup apps
///     went up for that reason, not because of the service. The report says so instead of letting
///     the numbers speak alone. Read-only.
/// </summary>
public static class BackgroundWorkProbe
{
    /// <summary>Windows Update's own workers: they run only while it downloads or installs.</summary>
    private static readonly string[] UpdateProcesses = ["TiWorker", "MoUsoCoreWorker", "wuauclt"];

    public const string WindowsUpdate = "Windows Update";
    public const string Store = "Microsoft Store";

    /// <summary>The installers running right now.</summary>
    public static IReadOnlySet<string> RunningNow()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in UpdateProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
                seen.Add(WindowsUpdate);
            foreach (var p in processes)
                p.Dispose();
        }
        return seen;
    }

    /// <summary>
    ///     What installed something between two moments: Windows Update (installation started or
    ///     finished) and the Store (an app package deployed).
    /// </summary>
    public static IReadOnlySet<string> InstalledBetween(DateTime fromUtc, DateTime toUtc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var window =
            $"TimeCreated[@SystemTime>='{fromUtc:yyyy-MM-ddTHH:mm:ss.fffZ}' and @SystemTime<='{toUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']";
        if (
            Any(
                "System",
                $"*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=43) and {window}]]"
            )
        )
            seen.Add(WindowsUpdate);
        if (
            Any(
                "Microsoft-Windows-AppXDeploymentServer/Operational",
                $"*[System[(EventID=400) and {window}]]"
            )
        )
            seen.Add(Store);
        return seen;
    }

    private static bool Any(string log, string xpath)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
            using var record = reader.ReadEvent();
            return record is not null;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return false; // a log that cannot be read says nothing either way
        }
    }
}
