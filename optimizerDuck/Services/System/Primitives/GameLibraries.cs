using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace optimizerDuck.Services.System.Primitives;

/// <summary>
///     Game library folders: Steam library "steamapps\common" folders and Epic Games install
///     folders. Used by the index exclusion and, behind <see cref="IsSafeToExclude(string)" />,
///     by the Defender exclusion (plan section 1.1: never Downloads, Temp, the Desktop or user
///     folders).
/// </summary>
/// <remarks>
///     Where the libraries are comes from HKCU and from Epic manifests in ProgramData, which any
///     user can write. So a folder is excluded only if it has a safe shape, is outside every
///     protected folder, and on disk really looks like a library. A library stays writable by
///     its user by design; plan section 1.1 accepts that.
/// </remarks>
public static partial class GameLibraries
{
    /// <summary>
    ///     Characters a library path never needs: quotes (ASCII and typographic, which close a
    ///     PowerShell literal), PowerShell and environment variable syntax, Defender wildcards,
    ///     command separators and "~" (8.3 short names hide which folder it really is).
    /// </summary>
    private static readonly char[] ForbiddenChars =
    [
        '\'',
        '\u2018',
        '\u2019',
        '\u201A',
        '\u201B',
        '"',
        '`',
        '$',
        '%',
        '*',
        '?',
        ';',
        '~',
    ];

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
    ///     Whether a folder may be excluded from Defender: a plain local path, outside (and not
    ///     a parent of) Windows, Program Files, ProgramData, every user profile, Temp and the
    ///     user's libraries, on a fixed disk, with no junction or symlink on the way, and with
    ///     the marks of a real library (Steam app manifests, Epic's ".egstore").
    /// </summary>
    public static bool IsSafeToExclude(string folder) =>
        IsSafeToExclude(folder, ProtectedFolders()) && LooksLikeLibraryOnDisk(folder.TrimEnd('\\'));

    /// <summary>The shape and protected-folder checks, without touching the disk.</summary>
    internal static bool IsSafeToExclude(string folder, IEnumerable<string> protectedFolders)
    {
        if (!HasSafeShape(folder))
            return false;
        var path = folder.TrimEnd('\\');
        if (path.Length <= 3)
            return false;
        foreach (var p in protectedFolders.Select(p => p.TrimEnd('\\')).Where(p => p.Length > 0))
        {
            if (path.Equals(p, StringComparison.OrdinalIgnoreCase))
                return false;
            if (p.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) // parent of a protected folder
                return false;
            if (
                path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)
                && !(IsProgramFiles(p) && IsProgramFilesGameFolder(path, p))
            )
                return false;
        }
        return true;
    }

    /// <summary>
    ///     A drive-letter path ("D:\..."), already normalized (no ".", "..", doubled separators
    ///     or trailing dots), with none of <see cref="ForbiddenChars" />. Rejects UNC paths,
    ///     "\\?\" paths, relative paths and 8.3 short names.
    /// </summary>
    internal static bool HasSafeShape(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return false;
        if (folder.Any(c => char.IsControl(c) || Array.IndexOf(ForbiddenChars, c) >= 0))
            return false;
        if (!DriveRooted().IsMatch(folder))
            return false;
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception ex)
            when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return full.TrimEnd('\\').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProgramFiles(string folder) =>
        folder.Equals(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            StringComparison.OrdinalIgnoreCase
        )
        || folder.Equals(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    ///     Inside Program Files only a Steam library ("...\steamapps\common") or one Epic game
    ///     ("Epic Games\&lt;game&gt;") is fine; any other subfolder (Windows Defender,
    ///     WindowsApps, Common Files, Edge) is not.
    /// </summary>
    private static bool IsProgramFilesGameFolder(string path, string programFiles)
    {
        if (path.EndsWith(@"\steamapps\common", StringComparison.OrdinalIgnoreCase))
            return true;
        var parts = path[(programFiles.Length + 1)..].Split('\\');
        return parts.Length == 2
            && parts[0].Equals("Epic Games", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     On a fixed disk, no reparse point (junction, symlink, mount point) from the root down,
    ///     and the marks of a library: Steam keeps "appmanifest_*.acf" next to "common"; Epic
    ///     keeps ".egstore" inside each game.
    /// </summary>
    private static bool LooksLikeLibraryOnDisk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null || new DriveInfo(root).DriveType != DriveType.Fixed)
                return false;
            for (
                var current = path;
                current.Length > root.Length;
                current = Path.GetDirectoryName(current)!
            )
                if (new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;

            if (path.EndsWith(@"\steamapps\common", StringComparison.OrdinalIgnoreCase))
                return Directory
                    .EnumerateFiles(Path.GetDirectoryName(path)!, "appmanifest_*.acf")
                    .Any();
            return Directory.Exists(Path.Combine(path, ".egstore"));
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<string> ProtectedFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return
        [
            windows,
            Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "Windows.old"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(profile) ?? string.Empty, // every profile, Public included
            Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty,
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("OneDrive") ?? string.Empty,
            Path.GetTempPath(),
            Downloads() ?? string.Empty,
        ];
    }

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

    [GeneratedRegex(@"^[A-Za-z]:\\[^\\]")]
    private static partial Regex DriveRooted();
}
