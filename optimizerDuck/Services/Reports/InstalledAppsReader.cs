using System.Globalization;
using System.IO;
using Microsoft.Win32;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Reports;

/// <summary>An installed program for the "heaviest programs" list (ACT-HEAVY-APPS).</summary>
public sealed record InstalledApp(string Name, long SizeKb, DateOnly? InstalledOn);

/// <summary>Where an Uninstall entry was found.</summary>
public enum InstallScope
{
    Machine64,
    Machine32,
    User,
}

/// <summary>One program of the inventory (plan F3-06), as its installer declared it.</summary>
public sealed record InventoryEntry(
    string Name,
    string? Version,
    string? Publisher,
    DateOnly? InstalledOn,
    long? SizeKb,
    InstallScope Scope
);

/// <summary>
///     Installed programs from the Uninstall keys (machine 64/32-bit and the client's user), with
///     what their installers declare. The last time a program was used is not tracked reliably by
///     Windows, so the report says "not measurable" instead. Local only: the list of programs never
///     goes to the AI (plan section 8.1).
/// </summary>
public static class InstalledAppsReader
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledApp> TopBySize(int count) =>
        ReadAll()
            .Where(e => e.SizeKb is > 0)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(e => e.SizeKb)!)
            .OrderByDescending(e => e.SizeKb)
            .Take(count)
            .Select(e => new InstalledApp(e.Name, e.SizeKb!.Value, e.InstalledOn))
            .ToList();

    /// <summary>
    ///     Every real program entry, one per name and scope. The user entries are those of the
    ///     person at the PC, also when the technician elevated with another account (plan F2-05).
    /// </summary>
    public static IReadOnlyList<InventoryEntry> ReadAll()
    {
        var entries = new Dictionary<(string, InstallScope), InventoryEntry>();
        foreach (var scope in Enum.GetValues<InstallScope>())
        {
            try
            {
                using var uninstall = Open(scope);
                if (uninstall is null)
                    continue;
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(sub);
                    if (ReadEntry(key, scope) is { } entry)
                        entries.TryAdd((entry.Name.ToUpperInvariant(), scope), entry);
                }
            }
            catch (Exception ex)
                when (ex
                        is UnauthorizedAccessException
                            or global::System.Security.SecurityException
                            or IOException
                ) { }
        }
        return entries
            .Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Scope)
            .ToList();
    }

    private static RegistryKey? Open(InstallScope scope)
    {
        if (scope == InstallScope.User)
            return InteractiveUser.OpenCurrentUserKey(UninstallKey);
        using var root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            scope == InstallScope.Machine64 ? RegistryView.Registry64 : RegistryView.Registry32
        );
        return root.OpenSubKey(UninstallKey);
    }

    /// <summary>A real program entry for the heavy-apps list: needs a declared size.</summary>
    internal static InstalledApp? Read(RegistryKey? key) =>
        ReadEntry(key, InstallScope.Machine64) is { SizeKb: > 0 } e
            ? new InstalledApp(e.Name, e.SizeKb!.Value, e.InstalledOn)
            : null;

    /// <summary>A real program: has a name, is not a system component or an update of another one.</summary>
    internal static InventoryEntry? ReadEntry(RegistryKey? key, InstallScope scope)
    {
        if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
            return null;
        if (
            key.GetValue("SystemComponent") is int and 1
            || key.GetValue("ParentKeyName") is not null
        )
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
        return new InventoryEntry(
            name.Trim(),
            Text(key.GetValue("DisplayVersion")),
            Text(key.GetValue("Publisher")),
            installed,
            key.GetValue("EstimatedSize") is int size && size > 0 ? size : null,
            scope
        );
    }

    private static string? Text(object? value) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
