using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>
///     Version 1.1.2: the startup apps that come pre-checked after "Equipo lento". Security,
///     drivers and sync are never pre-checked.
/// </summary>
public class StartupSuggestionsTests
{
    [Theory]
    [InlineData(
        "Microsoft Teams",
        @"""C:\Program Files\WindowsApps\MSTeams\ms-teams.exe"" msteams:system-initiated",
        true
    )]
    [InlineData("Spotify", @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe /minimized", true)]
    [InlineData(
        "Discord",
        @"C:\Users\x\AppData\Local\Discord\Update.exe --processStart Discord.exe",
        true
    )]
    [InlineData(
        "Adobe CCXProcess",
        @"C:\Program Files (x86)\Adobe\Adobe Creative Cloud Experience\CCXProcess.exe",
        true
    )]
    [InlineData(
        "OneDrive",
        @"C:\Users\x\AppData\Local\Microsoft\OneDrive\OneDrive.exe /background",
        false
    )]
    [InlineData("SecurityHealth", @"C:\WINDOWS\system32\SecurityHealthSystray.exe", false)]
    [InlineData("RtkAudUService", @"C:\WINDOWS\System32\DriverStore\RtkAudUService64.exe", false)]
    [InlineData(
        "AMDNoiseSuppression",
        @"C:\WINDOWS\system32\AMD\ANR\AMDNoiseSuppression.exe",
        false
    )]
    [InlineData("Programa del cliente", @"C:\Contabilidad\agente.exe", false)]
    public void OnlyNonEssentialApps_ComePreChecked(string name, string command, bool expected) =>
        Assert.Equal(expected, StartupSuggestions.IsSuggested(name, command));
}
