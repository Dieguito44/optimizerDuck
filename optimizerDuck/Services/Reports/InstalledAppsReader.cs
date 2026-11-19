using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace optimizerDuck.Services.Reports;

/// <summary>An installed program for the "heaviest programs" list (ACT-HEAVY-APPS).</summary>
public sealed record InstalledApp(string Name, long SizeKb, DateOnly? InstalledOn);

/// <summary>
///     Installed programs from the Uninstall keys (machine 64/32-bit and user), with the size and
///     install date their installers declare. The last time a program was used is not tracked
///     reliably by Windows, so the report says "not measurable" instead. Local report only: the
///     list of programs never goes to the AI (plan section 8.1).
/// </summary>
public static class InstalledAppsReader
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledApp> TopBySize(int count)
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var (hive, view) in new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Default),
            }
        )
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null)
                    continue;
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(sub);
                    if (
                        Read(key) is { } app
                        && (!apps.TryGetValue(app.Name, out var seen) || seen.SizeKb < app.SizeKb)
                    )
                        apps[app.Name] = app;
                }
            }
            catch (Exception ex)
                when (ex
                        is UnauthorizedAccessException
                            or global::System.Security.SecurityException
                            or IOException
                ) { }
        }
        return apps.Values.OrderByDescending(a => a.SizeKb).Take(count).ToList();
    }

    /// <summary>A real program entry: has a name and a size, is not a system component or an update.</summary>
    internal static InstalledApp? Read(RegistryKey? key)
    {
        if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
            return null;
        if (
            key.GetValue("SystemComponent") is int and 1
            || key.GetValue("ParentKeyName") is not null
        )
            return null;
        if (key.GetValue("EstimatedSize") is not int size || size <= 0)
            return null;
        DateOnly? installed =
            key.GetValue("InstallDate") is string d
            && DateOnly.TryParseExact(
                d.Trim(),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date
            )
                ? date
                : null;
        return new InstalledApp(name.Trim(), size, installed);
    }
}
