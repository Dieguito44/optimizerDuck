using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace optimizerDuck.Services.System.Primitives;

/// <summary>
///     Game library folders: Steam library "steamapps\common" folders and Epic Games install
///     folders. Used by the index exclusion and, behind <see cref="IsSafeToExclude" />, by the
///     Defender exclusion (plan section 1.1: never Downloads, Temp, the Desktop or user folders).
/// </summary>
public static partial class GameLibraries
{
    public static IReadOnlyList<string> Folders()
    {
        var folders = new List<string>();

        using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (steam?.GetValue("SteamPath") is string steamPath)
            {
                var vdf = Path.Combine(
                    steamPath.Replace('/', '\\'),
                    "steamapps",
                    "libraryfolders.vdf"
                );
                if (File.Exists(vdf))
                    foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                        folders.Add(
                            Path.Combine(
                                m.Groups[1].Value.Replace(@"\\", @"\"),
                                "steamapps",
                                "common"
                            )
                        );
            }

        var epic = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests"
        );
        if (Directory.Exists(epic))
            foreach (var item in Directory.EnumerateFiles(epic, "*.item"))
                try
                {
                    if (
                        JObject.Parse(File.ReadAllText(item)).Value<string>("InstallLocation") is
                        { Length: > 0 } dir
                    )
                        folders.Add(dir);
                }
                catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException) { }

        return Normalize(folders);
    }

    internal static IReadOnlyList<string> Normalize(IEnumerable<string> folders) =>
        folders
            .Where(Directory.Exists)
            .Select(f => Path.GetFullPath(f).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    ///     Whether a folder may be excluded from Defender: not a drive root, and neither one of
    ///     (nor a parent of, nor inside) Windows, Program Files, ProgramData, Temp, the user
    ///     profile's top folders or AppData.
    /// </summary>
    public static bool IsSafeToExclude(string folder) =>
        IsSafeToExclude(folder, ProtectedFolders());

    internal static bool IsSafeToExclude(string folder, IEnumerable<string> protectedFolders)
    {
        var path = folder.TrimEnd('\\');
        if (path.Length <= 3 || Path.GetPathRoot(path)?.TrimEnd('\\') == path)
            return false;
        foreach (var p in protectedFolders.Select(p => p.TrimEnd('\\')).Where(p => p.Length > 0))
        {
            if (path.Equals(p, StringComparison.OrdinalIgnoreCase))
                return false;
            if (p.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) // parent of a protected folder
                return false;
            if (
                path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)
                && !IsProgramFilesGameFolder(path, p)
            )
                return false;
        }
        return true;
    }

    /// <summary>A game inside Program Files (e.g. Epic Games\Fortnite) is fine; Program Files itself is not.</summary>
    private static bool IsProgramFilesGameFolder(string path, string protectedFolder) =>
        protectedFolder.Equals(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            StringComparison.OrdinalIgnoreCase
        )
        || protectedFolder.Equals(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            StringComparison.OrdinalIgnoreCase
        );

    private static IEnumerable<string> ProtectedFolders() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            Downloads() ?? string.Empty,
        };

    /// <summary>The user's Downloads folder (Known Folder in the user shell folders key).</summary>
    public static string? Downloads()
    {
        using var shell = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
        );
        return shell?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string d
            ? Environment.ExpandEnvironmentVariables(d)
            : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();
}
