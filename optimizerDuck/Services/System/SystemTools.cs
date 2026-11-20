using System.Diagnostics;
using System.IO;

namespace optimizerDuck.Services.System;

/// <summary>
///     Plan of improvements F1-05 (audit Z-05): Zentra runs elevated, so a tool found through the
///     PATH or the current folder could be someone else's program. Every external process starts
///     from an absolute path under Windows, with System32 as its folder and a PATH and module path
///     made only of Windows folders. winget is taken from the App Installer package (not from the
///     alias in the user's WindowsApps folder), and only when Microsoft signed it.
/// </summary>
public static class SystemTools
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static string WindowsDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly Dictionary<string, Func<string>> Known = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["cmd"] = () => Path.Combine(System32, "cmd.exe"),
        ["powershell"] = () =>
            Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["dism"] = () => Path.Combine(System32, "dism.exe"),
        ["sfc"] = () => Path.Combine(System32, "sfc.exe"),
        ["chkdsk"] = () => Path.Combine(System32, "chkdsk.exe"),
        ["powercfg"] = () => Path.Combine(System32, "powercfg.exe"),
        ["compact"] = () => Path.Combine(System32, "compact.exe"),
        ["schtasks"] = () => Path.Combine(System32, "schtasks.exe"),
        ["reg"] = () => Path.Combine(System32, "reg.exe"),
        ["taskmgr"] = () => Path.Combine(System32, "taskmgr.exe"),
        ["control"] = () => Path.Combine(System32, "control.exe"),
        ["mmc"] = () => Path.Combine(System32, "mmc.exe"),
        ["explorer"] = () => Path.Combine(WindowsDir, "explorer.exe"),
    };

    /// <summary>
    ///     The absolute path for a tool name ("cmd", "dism.exe", "winget"...). A path that is
    ///     already absolute is kept. An unknown bare name is refused rather than searched for.
    /// </summary>
    public static string Resolve(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
            return fileName;
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Equals("winget", StringComparison.OrdinalIgnoreCase))
            return Winget() ?? throw NotFound("winget (App Installer, signed by Microsoft)");
        if (Known.TryGetValue(name, out var path))
            return path();
        throw NotFound($"'{fileName}' is not a known Windows tool; use an absolute path");
    }

    /// <summary>Like a failed Process.Start, so the callers' existing handling applies.</summary>
    private static global::System.ComponentModel.Win32Exception NotFound(string what) =>
        new(2, what); // ERROR_FILE_NOT_FOUND

    /// <summary>Folders the child processes may search: only Windows' own.</summary>
    public static string SafePath =>
        string.Join(
            ';',
            System32,
            WindowsDir,
            Path.Combine(System32, "Wbem"),
            Path.Combine(System32, "WindowsPowerShell", "v1.0"),
            Path.Combine(System32, "OpenSSH")
        );

    /// <summary>PowerShell modules from Windows and Program Files only, never the user's Documents.</summary>
    public static string SafeModulePath =>
        string.Join(
            ';',
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsPowerShell",
                "Modules"
            ),
            Path.Combine(System32, "WindowsPowerShell", "v1.0", "Modules")
        );

    /// <summary>
    ///     Applies the rules to a process about to start: absolute file name, System32 as folder
    ///     (unless the caller chose one), and only Windows folders in PATH and PSModulePath.
    /// </summary>
    public static ProcessStartInfo Harden(ProcessStartInfo psi)
    {
        psi.FileName = Resolve(psi.FileName);
        if (string.IsNullOrEmpty(psi.WorkingDirectory))
            psi.WorkingDirectory = System32;
        if (!psi.UseShellExecute)
        {
            psi.Environment["PATH"] = SafePath;
            psi.Environment["PSModulePath"] = SafeModulePath;
        }
        return psi;
    }

    private static string? _winget;
    private static readonly Lock WingetLock = new();

    /// <summary>
    ///     winget.exe inside the newest installed App Installer package, when Microsoft signed it.
    ///     Null when App Installer is missing or the file is not Microsoft's.
    /// </summary>
    public static string? Winget()
    {
        lock (WingetLock)
        {
            if (_winget is not null && File.Exists(_winget))
                return _winget;
            _winget = FindWinget();
            return _winget;
        }
    }

    private const string AppInstallerFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    private static string? FindWinget()
    {
        var manager = new Windows.Management.Deployment.PackageManager();
        IEnumerable<Windows.ApplicationModel.Package> packages;
        try
        {
            // Every user's packages (needs administrator, which Zentra has).
            packages = manager.FindPackages(AppInstallerFamily).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            packages = manager.FindPackagesForUser(string.Empty, AppInstallerFamily).ToList();
        }

        foreach (
            var package in packages
                .OrderByDescending(p => p.Id.Version.Major)
                .ThenByDescending(p => p.Id.Version.Minor)
                .ThenByDescending(p => p.Id.Version.Build)
                .ThenByDescending(p => p.Id.Version.Revision)
        )
        {
            string? location;
            try
            {
                location = package.InstalledLocation?.Path;
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
                continue;
            }
            var exe = location is null ? null : Path.Combine(location, "winget.exe");
            if (
                exe is not null
                && File.Exists(exe)
                && Repair.SafetyScannerService.IsSignedByMicrosoft(exe)
            )
                return exe;
        }
        return null;
    }
}
