using System.Diagnostics;
using System.IO;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     Constants, links, install paths and the AppX package sets used by the bloatware scan.
/// </summary>
public static class Shared
{
    /// <summary>The ASCII-art logo shown in the startup log banner.</summary>
    public const string RawLogo = """
          _____           _
         |__  /___ _ __ | |_ _ __ __ _
           / // _ \ '_ \| __| '__/ _` |
          / /|  __/ | | | |_| | | (_| |
         /____\___|_| |_|\__|_|  \__,_|
         Based on optimizerDuck by fatDuck (GPL v3)
        """;

    /// <summary>The product name shown in the window title, logs and restore points.</summary>
    public const string AppName = "Zentra";

    /// <summary>The upstream project Zentra is forked from. Kept for GPL v3 attribution.</summary>
    public const string UpstreamRepoURL = "https://github.com/itsfatduck/optimizerDuck";

    /// <summary>The invite link to the upstream project's Discord server.</summary>
    public const string DiscordInviteURL = "https://discord.gg/tDUBDCYw9Q";

    /// <summary>The upstream project documentation site (technical base of Zentra).</summary>
    public const string WebsiteURL = "https://optimizerduck.vercel.app/";

    /// <summary>The project's GitHub repository (the Zentra fork).</summary>
    public const string GitHubRepoURL = "https://github.com/Dieguito44/optimizerDuck";

    /// <summary>The community page on the documentation site.</summary>
    public const string CommunityURL = "https://optimizerduck.vercel.app/docs/community";

    /// <summary>The contribution guide on the documentation site.</summary>
    public const string ContributeURL = "https://optimizerduck.vercel.app/docs/contribute/overview";

    /// <summary>The third-party notices file on GitHub.</summary>
    public const string AcknowledgementsURL =
        "https://github.com/Dieguito44/optimizerDuck/blob/master/THIRD-PARTY-NOTICES.md";

    /// <summary>The name given to the restore point created before an apply.</summary>
    public const string RestorePointName = "Zentra Restore Point";

    /// <summary>The GUID the bundled power plan is installed under.</summary>
    public const string PowerPlanGUID = "8ae61178-2c55-43f2-afb2-f83725823657";

    /// <summary>The full path of the running executable.</summary>
    public static readonly string ExePath = Environment.ProcessPath!;

    /// <summary>The directory that contains the running executable.</summary>
    public static readonly string ExeDir = Path.GetDirectoryName(ExePath)!;

    /// <summary>The file name of the running executable.</summary>
    public static readonly string ExeName = Path.GetFileName(ExePath);

    /// <summary>The file version of the running application.</summary>
    public static readonly string FileVersion = typeof(Shared).Assembly.GetName().Version is { } ver
        ? ver.Build >= 0
            ? $"{ver.Major}.{ver.Minor}.{ver.Build}"
            : $"{ver.Major}.{ver.Minor}"
        : (FileVersionInfo.GetVersionInfo(ExePath).FileVersion ?? "1.0.0");

    /// <summary>AppX package names the bloatware scan marks as safe to remove.</summary>
    public static readonly HashSet<string> SafeApps = new()
    {
        // Bing / MSN / News
        "Microsoft.BingWeather",
        "Microsoft.BingNews",
        "Microsoft.News",
        "MicrosoftStart",
        "Microsoft.BingFinance",
        "Microsoft.BingSports",
        "Microsoft.BingFoodAndDrink",
        "Microsoft.BingHealthAndFitness",
        "Microsoft.BingTravel",
        "Microsoft.BingTranslator", // Win10
        // Help, Tips & Feedback
        "Microsoft.GetStarted",
        "Microsoft.WindowsTips",
        "Microsoft.WindowsFeedbackHub",
        // Communications (Legacy/Discontinued)
        "Microsoft.Messaging",
        "Microsoft.OneConnect",
        "Microsoft.People",
        "Microsoft.SkypeApp",
        "MicrosoftTeams", // Old Teams (personal)
        "MSTeams", // New Teams
        // Office (Web Wrappers & Legacy)
        "Microsoft.OneNote",
        "Microsoft.Todos",
        "Microsoft.MicrosoftOfficeHub",
        "Microsoft.Office.OneNote",
        "Microsoft.Office.Sway",
        "Microsoft.MicrosoftJournal",
        "Microsoft.MicrosoftPowerBIForWindows",
        // Social & Wallet (Legacy)
        "Microsoft.Wallet",
        "Microsoft.MSWallet",
        // Mixed Reality & 3D (Legacy, not in Win11 images)
        "Microsoft.Microsoft3DViewer",
        "Microsoft.MixedReality.Portal",
        "Microsoft.Print3D",
        "Microsoft.3DBuilder",
        "Microsoft.Paint3D",
        // Multimedia & Entertainment
        "Microsoft.ZuneVideo",
        "Microsoft.WindowsSoundRecorder",
        "Microsoft.StickyNotes",
        "Microsoft.MicrosoftStickyNotes",
        "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.WindowsAlarms",
        "Microsoft.549981C3F5F10", // Cortana
        // Legacy & Redundant Utilities
        "Microsoft.Windows.DevHome",
        "Microsoft.WindowsMaps",
        "Microsoft.XboxApp", // Old Xbox Console Companion (Discontinued)
        "Microsoft.Windows.AIHub", // Copilot+ AI Hub (Copilot+ PCs)
        "Microsoft.MinecraftUWP", // Minecraft (Win10 consumer images)
        "Microsoft.NetworkSpeedTest", // Win10
        "Drawboard.DrawboardPDF",
        // Third-party (commonly pre-installed on OEM machines)
        "SpotifyAB.SpotifyMusic",
        "Clipchamp.Clipchamp",
        "Disney.37853FC22B2CE",
        "Amazon.com.Amazon",
        "4DF9E0F8.Netflix",
        "BytedancePte.Ltd.TikTok",
        "king.com.CandyCrushSaga",
        "king.com.CandyCrushSodaSaga",
        "king.com.BubbleWitch3Saga",
        "AmazonVideo.PrimeVideo",
        "Facebook.Instagram",
        "FACEBOOK.FACEBOOK",
        "7EE7776C.LinkedInforWindows",
        "Flipboard",
        "iHeartRadio",
        "PandoraMediaInc",
        "TuneInRadio",
        "Duolingo-LearnLanguagesforFree",
        "PicsArt-PhotoStudio",
        "WinZipUniversal",
        "AdobeSystemsIncorporated.AdobePhotoshopExpress",
        "OneCalendar",
        "NYTCrossword",
        "MarchofEmpires",
        "flaregamesGmbH.RoyalRevolt",
        "Asphalt8Airborne",
        "FarmVille2CountryEscape",
        "HiddenCity",
        "CaesarsSlotsFreeCasino",
        "COOKINGFEVER",
        "DisneyMagicKingdoms",
        "PhototasticCollage",
        "AutodeskSketchBook",
        "PolarrPhotoEditorAcademicEdition",
        "Sidia.LiveWallpaper",
        "HULULLC.HULUPLUS",
        "SlingTV",
        "DolbyLaboratories.DolbyAccess", // OEM audio app
    };

    /// <summary>AppX package names the bloatware scan marks as risky to remove.</summary>
    public static readonly HashSet<string> CautionApps = new()
    {
        // Core Utilities (default handlers for common file types)
        "Microsoft.WindowsCalculator",
        "Microsoft.WindowsPhotos",
        "Microsoft.MSPaint",
        "Microsoft.Paint",
        "Microsoft.WindowsNotepad",
        "Microsoft.WindowsCamera",
        "Microsoft.ScreenSketch", // Snipping Tool
        // Default browser (tied to WebView2-based apps and Windows features)
        "Microsoft.MicrosoftEdge",
        "Microsoft.MicrosoftEdge.Stable",
        // System Tools
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller", // winget
        "Microsoft.WindowsTerminal",
        "Microsoft.WindowsTerminalPreview",
        "Microsoft.RemoteDesktop",
        "Microsoft.PowerAutomateDesktop",
        // Communication & Collaboration
        "Microsoft.WindowsCommunicationsApps", // Mail & Calendar
        "Microsoft.OutlookForWindows", // New Outlook
        "Microsoft.YourPhone", // Phone Link
        "MicrosoftWindows.CrossDevice", // Phone Link features in Explorer
        // Widgets & Search integration
        "Microsoft.StartExperiencesApp",
        "MicrosoftWindows.Client.WebExperience",
        "Microsoft.WidgetsPlatformRuntime",
        "Microsoft.BingSearch",
        // Default media handler
        "Microsoft.ZuneMusic", // Media Player
        // Security, Family & Support
        "MicrosoftCorporationII.MicrosoftFamily", // parental controls
        "MicrosoftCorporationII.MicrosoftSupportDiagnosticTool",
        "MicrosoftCorporationII.QuickAssist",
        "Microsoft.GetHelp", // Required by some Windows 11 Troubleshooters
        // Cloud & Productivity
        "Microsoft.OneDrive",
        "Microsoft.Whiteboard",
        "Microsoft.M365Companions",
        // Media Extensions (Essential for file format support)
        "Microsoft.HEIFImageExtension",
        "Microsoft.WebMediaExtensions",
        "Microsoft.WebpImageExtension",
        "Microsoft.RawImageExtension",
        "Microsoft.VP9VideoExtensions",
        "Microsoft.AV1VideoExtension",
        "Microsoft.HEVCVideoExtension",
        "Microsoft.MPEG2VideoExtension",
        // Xbox / Gaming Services
        "Microsoft.XboxIdentityProvider",
        "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.XboxGameOverlay",
        "Microsoft.XboxGamingOverlay",
        "Microsoft.Xbox.TCUI",
        "Microsoft.GamingApp",
        // OEM Software (drivers, updates & support from the hardware vendor)
        "AD2F1837.HPAIExperienceCenter",
        "AD2F1837.HPConnectedMusic",
        "AD2F1837.HPConnectedPhotopoweredbySnapfish",
        "AD2F1837.HPDesktopSupportUtilities",
        "AD2F1837.HPEasyClean",
        "AD2F1837.HPFileViewer",
        "AD2F1837.HPJumpStarts",
        "AD2F1837.HPPCHardwareDiagnosticsWindows",
        "AD2F1837.HPPowerManager",
        "AD2F1837.HPPrinterControl",
        "AD2F1837.HPPrivacySettings",
        "AD2F1837.HPQuickDrop",
        "AD2F1837.HPQuickTouch",
        "AD2F1837.HPRegistration",
        "AD2F1837.HPSupportAssistant",
        "AD2F1837.HPSureShieldAI",
        "AD2F1837.HPSystemInformation",
        "AD2F1837.HPWelcome",
        "AD2F1837.HPWorkWell",
        "AD2F1837.myHP",
        "DellInc.DellSupportAssistforPCs",
        "DellInc.DellDigitalDelivery",
        "DellInc.DellMobileConnect",
        "E046963F.LenovoCompanion",
        "LenovoCompanyLimited.LenovoVantageService",
        "LGElectronics.LGMonitorApp",
        "EclipseManager", // Dell OEM
        "ACGMediaPlayer", // OEM media player
        "ActiproSoftwareLLC", // OEM-bundled UI components
        "CyberLinkMediaSuiteEssentials", // OEM media suite
    };

    /// <summary>
    ///     AppX packages Zentra never lists or removes (plan sections 1 and 6.2): Edge, WebView2,
    ///     the Store, winget, Windows Security, Calculator and Photos. Unlike
    ///     <see cref="CautionApps" />, this is a hard block, not a label.
    /// </summary>
    public static readonly HashSet<string> ProtectedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.MicrosoftEdge",
        "Microsoft.MicrosoftEdge.Stable",
        "Microsoft.MicrosoftEdgeDevToolsClient",
        "Microsoft.Win32WebViewHost",
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI",
        "Microsoft.WindowsCalculator",
        "Microsoft.WindowsPhotos",
    };

    /// <summary>Package name prefixes and fragments that are always protected.</summary>
    private static readonly string[] ProtectedAppPrefixes =
    [
        "Microsoft.VCLibs",
        "Microsoft.UI.Xaml",
        "Microsoft.NET.Native",
        "Microsoft.WindowsAppRuntime",
    ];

    private static readonly string[] ProtectedAppFragments = ["MicrosoftEdge", "WebView"];

    /// <summary>Whether an AppX package must never be listed for removal or removed.</summary>
    public static bool IsProtectedApp(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return false;

        return ProtectedApps.Contains(packageName)
            || ProtectedAppPrefixes.Any(prefix =>
                packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            )
            || ProtectedAppFragments.Any(fragment =>
                packageName.Contains(fragment, StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>The per-user data directory under LocalApplicationData.</summary>
    public static string RootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName
        );

    /// <summary>The directory that holds downloaded and bundled resources.</summary>
    public static string ResourcesDirectory => Path.Combine(RootDirectory, "Resources");

    /// <summary>The directory that holds downloaded resources.</summary>
    public static string DownloadsDirectory => Path.Combine(ResourcesDirectory, "Downloads");

    /// <summary>The directory that holds bundled assets.</summary>
    public static string AssetsDirectory => Path.Combine(ResourcesDirectory, "Assets");

    /// <summary>The directory that holds one revert file per applied item.</summary>
    public static string RevertDirectory => Path.Combine(RootDirectory, "Revert");

    /// <summary>
    ///     Where what an apply did is recorded, one file per item. Separate from the revert data,
    ///     so a report outlives the revert file it describes.
    /// </summary>
    public static string HistoryDirectory => Path.Combine(RootDirectory, "History");

    /// <summary>Gets a value that indicates whether the OS is Windows 11 or later.</summary>
    public static bool IsWindows11OrGreater =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
}
