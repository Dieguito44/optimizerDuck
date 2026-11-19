using System.IO;
using System.Text.RegularExpressions;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models.Services;

namespace optimizerDuck.Test.Domain.Optimizations;

/// <summary>
///     Zentra blacklist (plan section 1). Scans the app's source for changes that never belong
///     in the product: turning off Defender, Windows Update, UAC, BitLocker or Smart App Control,
///     timer bcdedit tweaks, disabling TCP auto-tuning, removing the pagefile, Edge or WebView2,
///     HOSTS blocking, RAM "freeing" and Windows/Office activators. A match fails the build gate.
/// </summary>
public class BlacklistTests
{
    /// <summary>A forbidden pattern and why it is forbidden.</summary>
    public sealed record Rule(string Name, Regex Pattern, string Reason);

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    ///     Protected services may be read, paused through native Windows settings or excluded
    ///     per folder (plan section 1.1), but never set to Disabled. The window stops at the end
    ///     of a statement so a multi-line <c>new ServiceItem(...)</c> is still caught.
    /// </summary>
    private const string ProtectedServices =
        "WinDefend|wuauserv|WaaSMedicSvc|UsoSvc|SecurityHealthService|WdNisSvc|mpssvc|BDESVC";

    public static readonly IReadOnlyList<Rule> Rules =
    [
        new(
            "ProtectedServiceDisabled",
            new Regex(
                $@"\b({ProtectedServices})\b[^;]{{0,250}}?(ServiceStartupType\.Disabled|start=\s*disabled|-StartupType\s+Disabled)",
                Options
            ),
            "Defender, Windows Update, firewall and BitLocker services are never disabled."
        ),
        new(
            "ProtectedServiceRegistry",
            new Regex($@"CurrentControlSet\\+Services\\+({ProtectedServices})\b", Options),
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
                @"DisableAntiSpyware|DisableAntiVirus|DisableRealtimeMonitoring|DisableBehaviorMonitoring|DisableIOAVProtection|DisableOnAccessProtection|DisableScriptScanning|DisableIntrusionPreventionSystem",
                Options
            ),
            "Defender is never turned off. Only folder exclusions for games (ACT-DEFENDER-EXCL)."
        ),
        new(
            "WindowsUpdateTurnedOff",
            new Regex(@"NoAutoUpdate|DisableWindowsUpdateAccess|SetDisableUXWUAccess", Options),
            "Windows Update is never turned off. Only the native 7-day pause (ACT-WU-PAUSE7)."
        ),
        new(
            "BitLockerTurnedOff",
            new Regex(@"manage-bde(\.exe)?\s+-off|Disable-BitLocker", Options),
            "BitLocker is never turned off."
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
            "TcpAutoTuningDisabled",
            new Regex(@"autotuninglevel\s*=\s*disabled", Options),
            "TCP auto-tuning is never disabled (ACT-TCP-NORMAL sets it back to normal)."
        ),
        new(
            "PagefileRemoved",
            new Regex(@"pagefileset|PagingFiles|AutomaticManagedPagefile\s*=\s*\$?false", Options),
            "The pagefile is never removed."
        ),
        new(
            "HostsFileBlocking",
            new Regex(@"drivers\\+etc\\+hosts", Options),
            "Microsoft domains are never blocked in the HOSTS file."
        ),
        new(
            "EdgeOrWebView2Removed",
            new Regex(
                // Either order: "Remove-AppxPackage ... Edge" or "Get-AppxPackage *Edge* | Remove-AppxPackage".
                @"(Remove-AppxPackage|Remove-AppxProvisionedPackage|winget(\.exe)?\s+uninstall|--uninstall)[^;]{0,250}?(MicrosoftEdge|Microsoft\.Edge|WebView2)"
                    + @"|(MicrosoftEdge|Microsoft\.Edge|WebView2)[^;]{0,250}?(Remove-AppxPackage|Remove-AppxProvisionedPackage|winget(\.exe)?\s+uninstall|--uninstall)",
                Options
            ),
            "Edge and WebView2 are never removed."
        ),
        new(
            "RamFreeing",
            new Regex(@"EmptyWorkingSet|SetProcessWorkingSetSize", Options),
            "RAM \"freeing\" is never done: it only moves pages to the pagefile."
        ),
        new(
            "Activators",
            new Regex(
                @"slmgr(\.vbs)?\s*/(ipk|skms|ato)|ospp\.vbs|massgrave|get\.activated\.win",
                Options
            ),
            "Windows and Office are never activated by the app."
        ),
    ];

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
    [InlineData("ProtectedServiceRegistry", @"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData("UacTouched", "\"EnableLUA\", 0")]
    [InlineData("DefenderTurnedOff", "Set-MpPreference -DisableRealtimeMonitoring $true")]
    [InlineData("WindowsUpdateTurnedOff", "\"NoAutoUpdate\", 1")]
    [InlineData("BitLockerTurnedOff", "manage-bde -off C:")]
    [InlineData("SmartAppControlTurnedOff", "VerifiedAndReputablePolicyState")]
    [InlineData("BcdeditTimers", "bcdedit /set useplatformclock true")]
    [InlineData("TcpAutoTuningDisabled", "netsh int tcp set global autotuninglevel=disabled")]
    [InlineData("PagefileRemoved", "wmic pagefileset delete")]
    [InlineData("HostsFileBlocking", @"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData("EdgeOrWebView2Removed", "Get-AppxPackage *MicrosoftEdge* | Remove-AppxPackage")]
    [InlineData(
        "EdgeOrWebView2Removed",
        "setup.exe --uninstall --msedgewebview --system-level WebView2"
    )]
    [InlineData("RamFreeing", "EmptyWorkingSet(handle)")]
    [InlineData("Activators", "irm https://get.activated.win | iex")]
    public void Rule_CatchesAKnownBadSample(string ruleName, string sample)
    {
        Assert.Matches(Rules.Single(r => r.Name == ruleName).Pattern, sample);
    }

    [Theory]
    [InlineData("netsh int tcp set global autotuninglevel=normal")]
    [InlineData("Add-MpPreference -ExclusionPath \"D:\\Games\"")]
    [InlineData("Set-MpPreference -PUAProtection Enabled")]
    [InlineData("new ServiceItem(\"Fax\", ServiceStartupType.Manual)")]
    [InlineData("Get-Service wuauserv")]
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

    private static readonly Lazy<string> _appSourceDirectory = new(FindAppSourceDirectory);

    private static string AppSourceDirectory => _appSourceDirectory.Value;

    private static IEnumerable<string> AppSourceFiles()
    {
        string[] extensions = [".cs", ".xaml", ".ps1", ".cmd", ".bat", ".reg", ".json"];
        return Directory
            .EnumerateFiles(AppSourceDirectory, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f =>
            {
                var relative = Path.GetRelativePath(AppSourceDirectory, f);
                return !relative.StartsWith("bin" + Path.DirectorySeparatorChar)
                    && !relative.StartsWith("obj" + Path.DirectorySeparatorChar);
            });
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
