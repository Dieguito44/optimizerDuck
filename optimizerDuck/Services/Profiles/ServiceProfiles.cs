using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.Profiles;

/// <summary>
///     The profiles besides the "Servicio estándar" (version 1.1.0, 20/11/2026, after the test on
///     another PC: the standard profile is about privacy, not speed). Each one is the standard
///     profile plus its own settings. Closed lists, like the standard one: only Safe and
///     Moderate settings that ask for no confirmation, never the Experto tab. Ideas checked
///     against WinUtil's presets (MIT).
/// </summary>
public static class ServiceProfiles
{
    /// <summary>Old PCs, mechanical disks, little RAM: less work in the background.</summary>
    public static readonly IReadOnlyList<Type> SlowPcExtra =
    [
        typeof(UserExperience.DisableVisualEffects),
        typeof(Performance.DisableBackgroundApps),
        typeof(Performance.DisableBrowserBackground),
        typeof(Performance.ExcludeLargeFoldersFromIndex),
        typeof(BloatwareAndServices.SetRarelyUsedServicesToManual),
    ];

    /// <summary>
    ///     PCs used to play. The GPU settings carry their own maker condition, so only the ones
    ///     for this PC's graphics apply. The power plan is left to the Energía tab: on a laptop it
    ///     would drain the battery.
    /// </summary>
    public static readonly IReadOnlyList<Type> GamingExtra =
    [
        typeof(Gpu.SetGamesToHighPerformanceGpu),
        typeof(Gpu.SetMonitorsToMaxRefresh),
        typeof(Gpu.AmdDisableUlps),
        typeof(Gpu.AmdDisableAspm),
        typeof(Gpu.NvidiaDisableDynamicPstate),
        typeof(Gpu.IntelDisableAsyncFlips),
        typeof(Gpu.IntelDisableAdaptiveVsync),
        typeof(PowerManagement.DisableUSBPowerSaving),
        typeof(Performance.DisableNicPowerSaving),
        typeof(Performance.OptimizeMultimediaScheduler),
        typeof(Performance.DisableBackgroundApps),
    ];

    /// <summary>Clients who want Windows to send as little as possible.</summary>
    public static readonly IReadOnlyList<Type> PrivacyExtra =
    [
        typeof(SecurityAndPrivacy.DisableTelemetry),
        typeof(SecurityAndPrivacy.DisableErrorReporting),
        typeof(SecurityAndPrivacy.DisableAutoLogger),
        typeof(SecurityAndPrivacy.DisableCopilot),
        typeof(SecurityAndPrivacy.DisableEdgeTelemetryAndAds),
        typeof(SecurityAndPrivacy.DisableWpbt),
        typeof(AI.DisableRecall),
    ];

    /// <summary>8 GB or less: Windows 11 with a browser open already uses most of it.</summary>
    public const long SlowPcMaxRamKb = 8L * 1024 * 1024;

    /// <summary>
    ///     The profile that fits this PC, with the reason in the technician's words: a mechanical
    ///     system disk or 8 GB of RAM or less → Equipo lento; a game library → Juegos; otherwise
    ///     the standard one. The technician can always choose another.
    /// </summary>
    public static (ServicePreset Preset, string Reason) Suggest(
        SystemInfo info,
        IReadOnlyList<string>? gameLibraries = null
    )
    {
        var system = info.Storage.Volumes.FirstOrDefault(v => v.IsSystemDrive);
        if (system?.MediaType == StorageMediaType.Hdd)
            return (ServicePreset.SlowPc, Loc.Instance["Profile.Reason.Hdd"]);
        if (info.Memory.TotalKB is > 0 and <= SlowPcMaxRamKb)
            return (
                ServicePreset.SlowPc,
                Loc.Instance[
                    "Profile.Reason.LowRam",
                    Math.Round(info.Memory.TotalKB / 1024.0 / 1024.0)
                ]
            );
        if ((gameLibraries ?? SafeGameLibraries()).Count > 0)
            return (ServicePreset.Gaming, Loc.Instance["Profile.Reason.Games"]);
        return (ServicePreset.Standard, Loc.Instance["Profile.Reason.Default"]);
    }

    private static IReadOnlyList<string> SafeGameLibraries()
    {
        try
        {
            return GameLibraries.Folders();
        }
        catch (Exception ex)
            when (ex
                    is global::System.IO.IOException
                        or UnauthorizedAccessException
                        or global::System.Security.SecurityException
            )
        {
            return [];
        }
    }
}
