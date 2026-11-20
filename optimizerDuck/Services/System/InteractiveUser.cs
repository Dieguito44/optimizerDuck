using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace optimizerDuck.Services.System;

/// <summary>
///     The person at the PC (plan of improvements F2-05, audit Z-17). When the client is a
///     standard user and the technician elevates Zentra with another administrator account,
///     HKCU and the per-user apps belong to that administrator, not to the client: the profile
///     would say "applied" and the client would see nothing change. In that case Zentra writes
///     the client's settings into HKEY_USERS\&lt;client SID&gt; instead.
/// </summary>
public static partial class InteractiveUser
{
    private static readonly Lazy<string?> Detected = new(Detect);

    /// <summary>
    ///     Where the SID comes from; replaced in tests (which pin "no redirection" so a build
    ///     machine's console user never changes what they write). Never read from the
    ///     environment or a file: a non-elevated process could set those.
    /// </summary>
    internal static Func<string?> Source { get; set; } = () => Detected.Value;

    /// <summary>
    ///     The SID of the console user when it is not the account Zentra runs as; null in the
    ///     normal case (same account, or nobody at the console).
    /// </summary>
    public static string? RedirectSid => Source();

    /// <summary>Whether HKCU and per-user apps are redirected to the console user.</summary>
    public static bool IsRedirected => RedirectSid is not null;

    [GeneratedRegex(@"^S-1-5-21(-\d+){4}$")]
    private static partial Regex LocalAccountSid();

    /// <summary>Whether a SID looks like a real user account (and is safe in a command line).</summary>
    internal static bool IsUserSid(string? sid) =>
        sid is not null && (LocalAccountSid().IsMatch(sid) || EntraSid().IsMatch(sid));

    [GeneratedRegex(@"^S-1-12-1(-\d+){4}$")]
    private static partial Regex EntraSid();

    private static string? Detect()
    {
        try
        {
            if (Diagnostics.ConsoleUser.IsCurrentProcessUser())
                return null;
            var sid = Diagnostics.ConsoleUser.ConsoleUserSid();
            // Only a hive that is loaded right now (the user is signed in) is ever used.
            if (!IsUserSid(sid))
                return null;
            using var hive = Registry.Users.OpenSubKey(sid!);
            return hive is null ? null : sid;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The root of the client's HKCU, writable: HKEY_USERS\&lt;SID&gt; when redirected, the
    ///     process HKCU otherwise. The caller disposes it (disposing HKCU itself is harmless).
    /// </summary>
    public static RegistryKey CurrentUserRoot() =>
        RedirectSid is { } sid
            // Plan F1-04: the root only needs to read and to create or open subkeys; the
            // subkeys are opened with the rights each write needs.
            ? Registry.Users.OpenSubKey(
                sid,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                global::System.Security.AccessControl.RegistryRights.ReadKey
                    | global::System.Security.AccessControl.RegistryRights.CreateSubKey
            ) ?? Registry.CurrentUser
            : Registry.CurrentUser;

    /// <summary>
    ///     The client's Startup folder. When redirected it comes from the client's own
    ///     "Shell Folders" key (already expanded), never from the administrator's profile.
    /// </summary>
    public static string StartupFolder()
    {
        if (RedirectSid is not { } sid)
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        using var folders = Registry.Users.OpenSubKey(
            $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"
        );
        return folders?.GetValue("Startup") as string ?? string.Empty;
    }

    /// <summary>The client's %LOCALAPPDATA% (plan F3-09: Chrome, Edge and Brave live there).</summary>
    public static string LocalAppDataFolder() =>
        ShellFolder("Local AppData", Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The client's %APPDATA% (Firefox profiles live there).</summary>
    public static string RoamingAppDataFolder() =>
        ShellFolder("AppData", Environment.SpecialFolder.ApplicationData);

    private static string ShellFolder(string valueName, Environment.SpecialFolder own)
    {
        if (RedirectSid is not { } sid)
            return Environment.GetFolderPath(own);
        using var folders = Registry.Users.OpenSubKey(
            $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"
        );
        return folders?.GetValue(valueName) as string ?? string.Empty;
    }

    /// <summary>Opens a key of the client's HKCU (or the process HKCU in the normal case).</summary>
    public static RegistryKey? OpenCurrentUserKey(string subPath, bool writable = false) =>
        RedirectSid is { } sid
            ? Registry.Users.OpenSubKey($@"{sid}\{subPath}", writable)
            : Registry.CurrentUser.OpenSubKey(subPath, writable);
}
