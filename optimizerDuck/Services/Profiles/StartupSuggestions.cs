using System.IO;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Optimizations.Models.StartupManager;

namespace optimizerDuck.Services.Profiles;

/// <summary>
///     Startup apps in the "Equipo lento" profile (version 1.1.2). The profile itself does not
///     touch them, yet they weigh most at boot: in the Hyper-V test the Store added Teams and the
///     idle RAM went up 348 MB. After the profile, the technician sees every enabled startup app;
///     the ones on this list come pre-checked, the client unchecks what they use. Security,
///     drivers and sync (OneDrive, Dropbox) never come pre-checked.
/// </summary>
public static class StartupSuggestions
{
    /// <summary>Fragments of the name or command of apps that do not need to start with Windows.</summary>
    private static readonly string[] NonEssential =
    [
        "Teams", "Spotify", "Discord", "Steam", "EpicGamesLauncher", "Epic Games",
        "Adobe Creative Cloud", "CCXProcess", "AdobeGCClient", "AdobeARM", "Acrobat",
        "Skype", "Zoom", "OfficeHub", "Microsoft 365", "Cortana", "iTunesHelper",
        "Opera", "Battle.net", "EADesktop", "Origin", "Ubisoft", "GOG Galaxy",
        "Messenger", "WhatsApp", "Telegram", "Copilot",
    ];

    /// <summary>Never pre-checked, even if a fragment above matches.</summary>
    private static readonly string[] Keep =
    [
        "SecurityHealth", "Defender", "OneDrive", "Dropbox", "Google Drive", "Realtek",
        "RtkAud", "Synaptics", "ELAN", "Intel", "NVIDIA", "AMD", "Radeon", "Bluetooth", "Logitech",
    ];

    /// <summary>Whether the app comes pre-checked for disabling.</summary>
    public static bool IsSuggested(string name, string command)
    {
        var text = name + " " + command;
        return !Keep.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))
            && NonEssential.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One app the profile disabled: enough to find it again and turn it back on.</summary>
    public sealed record DisabledApp(string Name, StartupAppLocation Location, string Key);

    // With the revert data: it must survive "Terminar servicio", which deletes the user's data.
    private static string StorePath => Path.Combine(Shared.StateDirectory, "profile-startup-disabled.json");

    public static IReadOnlyList<DisabledApp> LoadDisabled()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonConvert.DeserializeObject<List<DisabledApp>>(File.ReadAllText(StorePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void SaveDisabled(IEnumerable<DisabledApp> apps)
    {
        var all = LoadDisabled().Concat(apps).DistinctBy(a => (a.Location, a.Key)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonConvert.SerializeObject(all, Formatting.Indented));
    }

    public static void ClearDisabled()
    {
        try
        {
            File.Delete(StorePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string KeyOf(StartupApp app) =>
        string.IsNullOrEmpty(app.OriginalValueNameOrFileName) ? app.Name : app.OriginalValueNameOrFileName;
}
