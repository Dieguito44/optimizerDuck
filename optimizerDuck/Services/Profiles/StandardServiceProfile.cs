using optimizerDuck.Domain.Optimizations.Categories;

namespace optimizerDuck.Services.Profiles;

/// <summary>
///     The "Servicio estándar" profile (plan phase 6, step 1): the settings and the apps every
///     client gets by default. A closed list: a setting reaches clients only by being named here.
/// </summary>
public static class StandardServiceProfile
{
    /// <summary>
    ///     Safe, reversible settings that suit any PC. Left out on purpose:
    ///     the component store cleanup (irreversible; the diagnosis asks for it),
    ///     the GPU and power settings (they depend on the hardware and on whether it is a laptop;
    ///     the diagnosis picks them), the process priority and multimedia scheduler tweaks (no
    ///     measurable gain), keyboard repeat and visual effects (they change how the PC feels),
    ///     the WebDAV limit (rarely used), and every Moderate or Risky setting.
    /// </summary>
    public static readonly IReadOnlyList<Type> Settings =
    [
        // AI
        typeof(AI.DisableClickToDo),
        typeof(AI.SetAiServiceToManual),
        typeof(AI.DisableEdgeAiFeatures),
        typeof(AI.DisablePaintAiFeatures),
        typeof(AI.DisableNotepadAiFeatures),
        // Bloatware prevention
        typeof(BloatwareAndServices.DisablePreinstalledApps),
        typeof(BloatwareAndServices.PreventDeviceAppAutoDownload),
        // Network
        typeof(Performance.TcpAutoTuningNormal),
        // Privacy (ACT-PRIVACY-STD). Location and Find My Device are in by Diego's decision
        // (19/11/2026), as plain Safe settings that can be reverted.
        typeof(SecurityAndPrivacy.DisableAdvertisingAndSuggestions),
        typeof(SecurityAndPrivacy.DisableNewsAndInterests),
        typeof(SecurityAndPrivacy.HideMeetNowButton),
        typeof(SecurityAndPrivacy.DisableActivityHistory),
        typeof(SecurityAndPrivacy.DisableLocationAndSensors),
        typeof(SecurityAndPrivacy.DisableContentDeliveryManager),
        typeof(SecurityAndPrivacy.DisableFindMyDevice),
        typeof(SecurityAndPrivacy.DisableDeliveryOptimization),
        // Storage
        typeof(Storage.EnableStorageSense),
        typeof(Storage.EnableTrim),
        // User experience
        typeof(UserExperience.SpeedUpExplorerAndMenus),
        typeof(UserExperience.DisableStartMenuWebSearch),
        typeof(UserExperience.DisableStartRecommended),
        typeof(UserExperience.DisableSettings365Ads),
    ];

    /// <summary>
    ///     Plan section 6.2, "Safe (preseleccionado)" (ACT-BLOAT-STD), followed as written by
    ///     Diego's decision (19/11/2026), including GetHelp, Family and Power Automate, which
    ///     optimizerDuck marks "caution". These match the package name exactly.
    /// </summary>
    public static readonly IReadOnlyList<string> Apps =
    [
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.People",
        "Clipchamp.Clipchamp",
        "Microsoft.Todos",
        "MicrosoftCorporationII.MicrosoftFamily",
        "Microsoft.PowerAutomateDesktop",
    ];

    /// <summary>
    ///     Third-party games and promotions OEMs preinstall, as fragments of the package name (the
    ///     publisher prefix varies), taken from optimizerDuck's list. Streaming and social apps
    ///     (Netflix, Spotify, Instagram...) are not here: the client may use them.
    /// </summary>
    public static readonly IReadOnlyList<string> ThirdPartyGames =
    [
        "CandyCrushSaga",
        "CandyCrushSodaSaga",
        "BubbleWitch3Saga",
        "MarchofEmpires",
        "RoyalRevolt",
        "Asphalt8Airborne",
        "FarmVille2CountryEscape",
        "HiddenCity",
        "CaesarsSlotsFreeCasino",
        "COOKINGFEVER",
        "DisneyMagicKingdoms",
    ];

    /// <summary>Whether an installed package is one the profile removes.</summary>
    public static bool IsProfileApp(string packageName) =>
        Apps.Any(a => string.Equals(packageName, a, StringComparison.OrdinalIgnoreCase))
        || ThirdPartyGames.Any(g => packageName.Contains(g, StringComparison.OrdinalIgnoreCase));
}
