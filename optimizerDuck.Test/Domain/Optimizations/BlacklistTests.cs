using System.IO;
using System.Text.RegularExpressions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models.Services;

namespace optimizerDuck.Test.Domain.Optimizations;

/// <summary>
///     Zentra blacklist (plan section 1). Scans the app's source for changes that never belong
///     in the product: turning off Defender, Windows Update, UAC, the firewall, SmartScreen,
///     BitLocker or Smart App Control, timer and security bcdedit tweaks, disabling TCP
///     auto-tuning, removing the pagefile, Edge or WebView2, HOSTS blocking, RAM "freeing" and
///     Windows/Office activators. A match fails the build gate.
/// </summary>
/// <remarks>
///     A static scan cannot see names built at runtime. Runtime data paths (the bloatware page)
///     are covered by <see cref="Shared.IsProtectedApp" /> and its tests below.
/// </remarks>
public class BlacklistTests
{
    /// <summary>A forbidden pattern and why it is forbidden.</summary>
    public sealed record Rule(string Name, Regex Pattern, string Reason);

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    ///     Services that keep Windows secure and updated. They may be read, and Windows Update
    ///     may be paused through its native 7-day pause (plan section 1.1), but they are never
    ///     disabled, deleted or edited through their registry keys.
    /// </summary>
    private const string ProtectedServices =
        @"WinDefend|Sense|WdFilter|WdBoot|WdNisSvc|WdNisDrv|SecurityHealthService|wscsvc|mpssvc|BFE"
        + @"|wuauserv|WaaSMedicSvc|UsoSvc|DoSvc|BITS|TrustedInstaller|BDESVC|edgeupdate|edgeupdatem";

    private const string RemoveVerbs =
        @"Remove-AppxPackage|Remove-AppxProvisionedPackage|winget(\.exe)?\s+uninstall|--uninstall|--force-uninstall";

    private const string EdgeNames =
        @"MicrosoftEdge|Microsoft\.Edge|WebView2|EdgeWebView|--msedge\b|--msedgewebview";

    public static readonly IReadOnlyList<Rule> Rules =
    [
        new(
            "ProtectedServiceDisabled",
            new Regex(
                // C# form: new ServiceItem("WinDefend", ServiceStartupType.Disabled), also split over lines.
                $@"""({ProtectedServices})""\s*,\s*(ServiceStartupType\.Disabled|\(ServiceStartupType\)\s*4)"
                    // Shell forms, either order, within one command.
                    + $@"|\b({ProtectedServices})\b[^;\n|]{{0,120}}?(start=\s*disabled|-StartupType\s+Disabled)"
                    + $@"|(start=\s*disabled|-StartupType\s+Disabled)[^;\n|]{{0,120}}?\b({ProtectedServices})\b"
                    + $@"|\bsc(\.exe)?\s+delete\s+({ProtectedServices})\b",
                Options
            ),
            "Defender, Windows Update, firewall, BitLocker and Edge update services are never disabled."
        ),
        new(
            "ProtectedServiceRegistry",
            new Regex(
                $@"(CurrentControlSet|ControlSet\d{{3}})\\+Services\\+({ProtectedServices})\b",
                Options
            ),
            "Protected services are never changed through their registry keys."
        ),
        new(
            "UacTouched",
            new Regex(@"EnableLUA|ConsentPromptBehaviorAdmin|PromptOnSecureDesktop", Options),
            "UAC is never touched."
        ),
        new(
            "DefenderTurnedOff",
            new Regex(
                @"DisableAntiSpyware|DisableAntiVirus|DisableRealtimeMonitoring|DisableBehaviorMonitoring"
                    + @"|DisableIOAVProtection|DisableOnAccessProtection|DisableScriptScanning"
                    + @"|DisableIntrusionPreventionSystem|DisableBlockAtFirstSeen|DisableArchiveScanning"
                    + @"|TamperProtection|SpynetReporting|SubmitSamplesConsent|PUAProtection\s+(0|Disabled)"
                    + @"|(Uninstall|Remove)-WindowsFeature[^;\n]{0,80}Defender",
                Options
            ),
            "Defender is never turned off. Only folder exclusions for games (ACT-DEFENDER-EXCL)."
        ),
        new(
            "DefenderExclusionTooBroad",
            new Regex(
                @"-ExclusionExtension|-ExclusionProcess"
                    + @"|-ExclusionPath\s+['""]?([A-Za-z]:\\?['""\s]|\$env:(TEMP|TMP|USERPROFILE|APPDATA|LOCALAPPDATA)|[^'""\s]*(Downloads|Descargas|Desktop|Escritorio|\\Temp\b))",
                Options
            ),
            "Defender exclusions only cover the games library folder, never drives or user folders."
        ),
        new(
            "FirewallTurnedOff",
            new Regex(
                @"advfirewall[^;\n]{0,80}state\s+off|Set-NetFirewallProfile[^;\n]{0,120}-Enabled\s+(False|0|\$false)|EnableFirewall",
                Options
            ),
            "The Windows firewall is never turned off."
        ),
        new(
            "SmartScreenTurnedOff",
            new Regex(
                @"SmartScreenEnabled|EnableSmartScreen|EnableWebContentEvaluation|ShellSmartScreenLevel",
                Options
            ),
            "SmartScreen is never turned off."
        ),
        new(
            "WindowsUpdateTurnedOff",
            new Regex(
                @"NoAutoUpdate|DisableWindowsUpdateAccess|SetDisableUXWUAccess|FlightSettingsMaxPauseDays"
                    + @"|\bAUOptions\b|UseWUServer|\bWUServer\b|DoNotConnectToWindowsUpdateInternetLocations"
                    + @"|Microsoft\\+Windows\\+(WindowsUpdate|UpdateOrchestrator|WaaSMedic)\b",
                Options
            ),
            "Windows Update is never turned off. Only the native 7-day pause (ACT-WU-PAUSE7)."
        ),
        new(
            "BitLockerTurnedOff",
            new Regex(
                @"manage-bde(\.exe)?\s+-off|manage-bde(\.exe)?[^;\n]{0,40}-protectors\s+-disable|(Disable|Suspend)-BitLocker",
                Options
            ),
            "BitLocker is never turned off or suspended."
        ),
        new(
            "SmartAppControlTurnedOff",
            new Regex(@"VerifiedAndReputablePolicyState", Options),
            "Smart App Control is never turned off."
        ),
        new(
            "BcdeditTimers",
            new Regex(@"useplatformclock|useplatformtick|disabledynamictick", Options),
            "Timer bcdedit tweaks are never applied."
        ),
        new(
            "BcdeditSecurity",
            new Regex(@"nointegritychecks|testsigning|\bnx\s+AlwaysOff", Options),
            "Boot security (driver signing, DEP) is never lowered."
        ),
        new(
            "TcpAutoTuningDisabled",
            new Regex(
                @"autotuninglevel\s*=\s*(disabled|restricted|highlyrestricted)|AutoTuningLevelLocal\s+(Disabled|Restricted|HighlyRestricted)",
                Options
            ),
            "TCP auto-tuning is never disabled (ACT-TCP-NORMAL sets it back to normal)."
        ),
        new(
            "PagefileRemoved",
            new Regex(
                @"pagefileset|Win32_PageFileSetting|PagingFiles|AutomaticManagedPagefile\s*=\s*\$?false",
                Options
            ),
            "The pagefile is never removed."
        ),
        new(
            "HostsFileBlocking",
            new Regex(@"drivers[\\/""',\s]+etc[\\/""',\s]+hosts\b", Options),
            "Microsoft domains are never blocked in the HOSTS file."
        ),
        new(
            "EdgeOrWebView2Removed",
            new Regex(
                // Either order: "Remove-AppxPackage ... Edge" or "Get-AppxPackage *Edge* | Remove-AppxPackage".
                $@"({RemoveVerbs})[^;]{{0,250}}?({EdgeNames})|({EdgeNames})[^;]{{0,250}}?({RemoveVerbs})",
                Options
            ),
            "Edge and WebView2 are never removed."
        ),
        new(
            "RamFreeing",
            new Regex(
                @"EmptyWorkingSet|SetProcessWorkingSetSize|EmptyStandbyList|SystemMemoryListInformation",
                Options
            ),
            "RAM \"freeing\" is never done: it only moves pages to the pagefile."
        ),
        new(
            "Activators",
            new Regex(
                @"slmgr(\.vbs)?\s*[-/](ipk|skms|ato)|ospp\.vbs|massgrave|get\.activated\.win|KMS_VL_ALL|\bkms38\b|\bohook\b|TSforge",
                Options
            ),
            "Windows and Office are never activated by the app."
        ),
    ];

    /// <summary>
    ///     The only services the app may set to Disabled today (upstream privacy tweaks, none in the
    ///     blacklist). Growing this list needs a reason: mass-disabling services is blacklisted.
    /// </summary>
    private static readonly HashSet<string> DisabledServicesAllowList = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "DiagTrack",
        "dmwappushservice",
        "DcpSvc",
        "diagnosticshub.standardcollector.service",
        "WerSvc",
        "PcaSvc",
    };

    public static TheoryData<string> RuleNames()
    {
        var data = new TheoryData<string>();
        foreach (var rule in Rules)
            data.Add(rule.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void AppSource_DoesNotMatchBlacklistRule(string ruleName)
    {
        var rule = Rules.Single(r => r.Name == ruleName);
        var hits = new List<string>();

        foreach (var file in AppSourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in rule.Pattern.Matches(text))
            {
                var line = text[..match.Index].Count(c => c == '\n') + 1;
                hits.Add($"{Path.GetRelativePath(AppSourceDirectory, file)}:{line}: {match.Value}");
            }
        }

        Assert.True(
            hits.Count == 0,
            $"{rule.Reason}{Environment.NewLine}{string.Join(Environment.NewLine, hits)}"
        );
    }

    [Theory]
    [InlineData(
        "ProtectedServiceDisabled",
        "new ServiceItem(\"wuauserv\", ServiceStartupType.Disabled)"
    )]
    [InlineData(
        "ProtectedServiceDisabled",
        "new(\n    \"WinDefend\",\n    ServiceStartupType.Disabled\n)"
    )]
    [InlineData("ProtectedServiceDisabled", "sc config WaaSMedicSvc start= disabled")]
    [InlineData("ProtectedServiceDisabled", "Set-Service -StartupType Disabled -Name wuauserv")]
    [InlineData("ProtectedServiceDisabled", "sc.exe delete WinDefend")]
    [InlineData("ProtectedServiceRegistry", @"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData("ProtectedServiceRegistry", @"HKLM\SYSTEM\ControlSet001\Services\wuauserv")]
    [InlineData("UacTouched", "\"EnableLUA\", 0")]
    [InlineData("DefenderTurnedOff", "Set-MpPreference -DisableRealtimeMonitoring $true")]
    [InlineData("DefenderTurnedOff", "Set-MpPreference -PUAProtection Disabled")]
    [InlineData("DefenderExclusionTooBroad", "Add-MpPreference -ExclusionPath 'C:\\'")]
    [InlineData("DefenderExclusionTooBroad", "Add-MpPreference -ExclusionPath $env:TEMP")]
    [InlineData("DefenderExclusionTooBroad", "Add-MpPreference -ExclusionExtension .exe")]
    [InlineData("FirewallTurnedOff", "netsh advfirewall set allprofiles state off")]
    [InlineData("FirewallTurnedOff", "Set-NetFirewallProfile -Profile Domain -Enabled False")]
    [InlineData("SmartScreenTurnedOff", "\"EnableSmartScreen\", 0")]
    [InlineData("WindowsUpdateTurnedOff", "\"NoAutoUpdate\", 1")]
    [InlineData("WindowsUpdateTurnedOff", "\"FlightSettingsMaxPauseDays\", 0xFFFFFFFF")]
    [InlineData("WindowsUpdateTurnedOff", @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan")]
    [InlineData("BitLockerTurnedOff", "manage-bde -off C:")]
    [InlineData("BitLockerTurnedOff", "Suspend-BitLocker -MountPoint C:")]
    [InlineData("SmartAppControlTurnedOff", "VerifiedAndReputablePolicyState")]
    [InlineData("BcdeditTimers", "bcdedit /set useplatformclock true")]
    [InlineData("BcdeditSecurity", "bcdedit /set testsigning on")]
    [InlineData("TcpAutoTuningDisabled", "netsh int tcp set global autotuninglevel=disabled")]
    [InlineData(
        "TcpAutoTuningDisabled",
        "Set-NetTCPSetting -SettingName Internet -AutoTuningLevelLocal Disabled"
    )]
    [InlineData("PagefileRemoved", "wmic pagefileset delete")]
    [InlineData("PagefileRemoved", "Get-CimInstance Win32_PageFileSetting | Remove-CimInstance")]
    [InlineData("HostsFileBlocking", @"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData("HostsFileBlocking", "C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("HostsFileBlocking", "Path.Combine(system32, \"drivers\", \"etc\", \"hosts\")")]
    [InlineData("EdgeOrWebView2Removed", "Get-AppxPackage *MicrosoftEdge* | Remove-AppxPackage")]
    [InlineData(
        "EdgeOrWebView2Removed",
        "setup.exe --uninstall --msedge --system-level --force-uninstall"
    )]
    [InlineData("EdgeOrWebView2Removed", "setup.exe --uninstall --msedgewebview --system-level")]
    [InlineData("RamFreeing", "EmptyWorkingSet(handle)")]
    [InlineData("RamFreeing", "EmptyStandbyList.exe standbylist")]
    [InlineData("Activators", "irm https://get.activated.win | iex")]
    [InlineData("Activators", "slmgr -ato")]
    public void Rule_CatchesAKnownBadSample(string ruleName, string sample)
    {
        Assert.Matches(Rules.Single(r => r.Name == ruleName).Pattern, sample);
    }

    [Theory]
    [InlineData("netsh int tcp set global autotuninglevel=normal")]
    [InlineData("Add-MpPreference -ExclusionPath \"D:\\Juegos\\SteamLibrary\"")]
    [InlineData("Set-MpPreference -PUAProtection Enabled")]
    [InlineData("new ServiceItem(\"Fax\", ServiceStartupType.Manual)")]
    [InlineData("new(\"BITS\", ServiceStartupType.AutomaticDelayedStart)")]
    [InlineData("Get-Service wuauserv")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings\PauseUpdatesExpiryTime")]
    public void Rules_AllowTheControlledVariants(string sample)
    {
        Assert.DoesNotContain(Rules, rule => rule.Pattern.IsMatch(sample));
    }

    [Fact]
    public void Scanner_SeesTheAppSource()
    {
        // Guards against a silent pass when the source directory is not found.
        Assert.True(AppSourceFiles().Count() > 100);
    }

    [Fact]
    public void RarelyUsedServices_AreOnlySetToManual()
    {
        var services = BloatwareAndServices.SetRarelyUsedServicesToManual.Services;

        Assert.Equal(
            ["Fax", "MapsBroker", "RetailDemo", "wisvc", "WMPNetworkSvc"],
            services.Select(s => s.Name)
        );
        Assert.All(services, s => Assert.Equal(ServiceStartupType.Manual, s.StartupType));
    }

    [Fact]
    public void ServicesSetToDisabled_ComeOnlyFromTheAllowList()
    {
        // Only service item constructors: new("Name", ...) or new ServiceItem("Name", ...).
        var pattern = new Regex(
            @"\bnew\s*(ServiceItem\s*)?\(\s*""([^""]+)""\s*,\s*ServiceStartupType\.Disabled",
            Options
        );
        var found = AppSourceFiles()
            .SelectMany(file => pattern.Matches(File.ReadAllText(file)))
            .Select(match => match.Groups[2].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var outside = found.Where(name => !DisabledServicesAllowList.Contains(name)).ToList();

        // The upstream privacy tweaks do disable a few services: seeing none means the scan broke.
        Assert.Contains("DiagTrack", found, StringComparer.OrdinalIgnoreCase);
        Assert.True(
            outside.Count == 0,
            "Services set to Disabled outside the allow-list: " + string.Join(", ", outside)
        );
    }

    [Theory]
    [InlineData("Microsoft.MicrosoftEdge.Stable")]
    [InlineData("Microsoft.MicrosoftEdge")]
    [InlineData("Microsoft.Win32WebViewHost")]
    [InlineData("Microsoft.WebView2Runtime")]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.NET.Native.Framework.2.2")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.WindowsPhotos")]
    [InlineData("microsoft.microsoftedge.stable")]
    public void IsProtectedApp_BlocksPackagesThatAreNeverRemoved(string packageName)
    {
        Assert.True(Shared.IsProtectedApp(packageName));
    }

    [Theory]
    [InlineData("Microsoft.BingNews")]
    [InlineData("Microsoft.MicrosoftSolitaireCollection")]
    [InlineData("Clipchamp.Clipchamp")]
    [InlineData("")]
    [InlineData(null)]
    public void IsProtectedApp_LetsRemovableAppsThrough(string? packageName)
    {
        Assert.False(Shared.IsProtectedApp(packageName));
    }

    [Fact]
    public void SafeApps_NeverContainAProtectedApp()
    {
        Assert.DoesNotContain(Shared.SafeApps, Shared.IsProtectedApp);
    }

    private static readonly Lazy<string> _appSourceDirectory = new(FindAppSourceDirectory);

    private static string AppSourceDirectory => _appSourceDirectory.Value;

    /// <summary>
    ///     Every text file of the app project, whatever its extension, except build output and
    ///     translations (.resx holds user-facing text, not behavior).
    /// </summary>
    private static IEnumerable<string> AppSourceFiles()
    {
        return Directory
            .EnumerateFiles(AppSourceDirectory, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relative = Path.GetRelativePath(AppSourceDirectory, f);
                return !relative.StartsWith("bin" + Path.DirectorySeparatorChar)
                    && !relative.StartsWith("obj" + Path.DirectorySeparatorChar)
                    && !f.EndsWith(".resx", StringComparison.OrdinalIgnoreCase);
            })
            .Where(IsTextFile);
    }

    private static bool IsTextFile(string path)
    {
        Span<byte> head = stackalloc byte[4096];
        using var stream = File.OpenRead(path);
        var read = stream.Read(head);
        return !head[..read].Contains((byte)0);
    }

    private static string FindAppSourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "optimizerDuck", "optimizerDuck.csproj");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
        }

        throw new DirectoryNotFoundException("Could not locate the app source directory.");
    }
}
