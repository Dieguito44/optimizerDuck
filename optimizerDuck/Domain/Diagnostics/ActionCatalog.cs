using System.Collections.Frozen;
using optimizerDuck.Domain.UI;

namespace optimizerDuck.Domain.Diagnostics;

/// <summary>Catalog action ids (plan section 6.1). Nothing outside this list is ever recommended.</summary>
public static class ActionIds
{
    public const string StartupReview = "ACT-STARTUP-REVIEW";
    public const string DismCleanup = "ACT-DISM-CLEANUP";
    public const string DiskCleanup = "ACT-DISKCLEANUP";
    public const string StorageSense = "ACT-STORAGESENSE";
    public const string Trim = "ACT-TRIM";
    public const string TcpNormal = "ACT-TCP-NORMAL";
    public const string RepairWindows = "ACT-REPAIR-WIN";
    public const string PowerPlan = "ACT-POWER-PLAN";
    public const string PrivacyStandard = "ACT-PRIVACY-STD";
    public const string BloatStandard = "ACT-BLOAT-STD";
    public const string DriverGpu = "ACT-DRIVER-GPU";
    public const string DriverChipset = "ACT-DRIVER-CHIPSET";
    public const string BiosXmp = "ACT-BIOS-XMP";
    public const string BiosUma2G = "ACT-BIOS-UMA2G";
    public const string BiosUpdate = "ACT-BIOS-UPDATE";
    public const string HardwareReplaceSsd = "ACT-HW-REPLACE-SSD";
    public const string HardwareRamDual = "ACT-HW-RAM-DUAL";
    public const string HardwareStorage = "ACT-HW-STORAGE";
    public const string Backup = "ACT-BACKUP";
    public const string CompactOs = "ACT-COMPACTOS";
    public const string DismResetBase = "ACT-DISM-RESETBASE";
    public const string GameModeAdvanced = "ACT-GAMEMODE-ADV";
    public const string MonitorHz = "ACT-MON-HZ";
    public const string MpoOff = "ACT-MPO-OFF";
    public const string MonitorCable = "ACT-MON-CABLE";
    public const string GpuPreference = "ACT-GPU-PREF";
    public const string WingetUpgrade = "ACT-WINGET-UPGRADE";
    public const string DefenderFull = "ACT-DEFENDER-FULL";
    public const string DefenderOffline = "ACT-DEFENDER-OFFLINE";
    public const string PuaOn = "ACT-PUA-ON";
    public const string BrowserBackground = "ACT-BROWSER-BG";
    public const string HeavyApps = "ACT-HEAVY-APPS";
    public const string BackupSetup = "ACT-BACKUP-SETUP";
    public const string NicPower = "ACT-NIC-POWER";
    public const string IndexExclude = "ACT-INDEX-EXCLUDE";
    public const string HardwareBattery = "ACT-HW-BATTERY";
    public const string HardwareClean = "ACT-HW-CLEAN";
    public const string HardwareNetCable = "ACT-HW-NETCABLE";
    public const string WindowsUpdatePause7 = "ACT-WU-PAUSE7";
    public const string DefenderExclusion = "ACT-DEFENDER-EXCL";
    public const string ServicesManual = "ACT-SERVICES-MANUAL";
    public const string FastStartupToggle = "ACT-FASTSTARTUP-TOGGLE";

    // Plan of improvements, block 2B: steps the technician takes by hand.
    public const string BiosSecureBoot = "ACT-BIOS-SECUREBOOT";
    public const string SecureBootCertificate = "ACT-SECUREBOOT-CERT";
    public const string WindowsUpdateRestore = "ACT-WU-RESTORE";

    // Plan of improvements, block 3C.
    public const string Retrim = "ACT-RETRIM";
    public const string DnsReview = "ACT-DNS-REVIEW";
    public const string BrowserReview = "ACT-BROWSER-REVIEW";

    // Plan of improvements, block 4A.
    public const string DeviceDriver = "ACT-DEVICE-DRIVER";
    public const string DriverWindowsUpdate = "ACT-DRIVER-WU";
    public const string PowerMode = "ACT-POWER-MODE";

    // Version 1.2.0, extended diagnosis: steps the technician takes by hand.
    public const string Runtimes = "ACT-RUNTIMES";
    public const string ClockSync = "ACT-CLOCK-SYNC";
    public const string SsdTool = "ACT-SSD-TOOL";
    public const string AntivirusReview = "ACT-AV-REVIEW";
}

/// <summary>Who carries out an action.</summary>
public enum ActionKind
{
    /// <summary>Zentra applies it.</summary>
    Software,

    /// <summary>The technician does it by hand in Windows.</summary>
    Manual,

    /// <summary>The technician or the client changes it in the BIOS.</summary>
    ManualBios,

    /// <summary>Needs a hardware purchase or a visit.</summary>
    Hardware,

    /// <summary>Only a report, nothing changes.</summary>
    Informational,
}

/// <summary>Whether the change can be undone.</summary>
public enum ActionReversibility
{
    Reversible,
    Irreversible,

    /// <summary>Nothing on the PC changes (a scan, a report, a recommendation).</summary>
    NotApplicable,
}

/// <summary>Where an action comes from.</summary>
public enum ActionOrigin
{
    /// <summary>A diagnostic rule recommends it.</summary>
    RuleDriven,

    /// <summary>Part of a service profile or applied on the client's request.</summary>
    ProfileOrOnRequest,
}

/// <summary>A catalog entry.</summary>
public sealed record ActionDefinition(
    string Id,
    ActionKind Kind,
    OptimizationRisk? Risk,
    ActionReversibility Reversibility,
    ActionOrigin Origin
)
{
    /// <summary>Localization key of the action title, e.g. "Diagnostic.Action.ACT-TRIM".</summary>
    public string TitleKey => $"Diagnostic.Action.{Id}";
}

/// <summary>The closed action catalog (plan section 6.1).</summary>
public static class ActionCatalog
{
    private const ActionOrigin Rule = ActionOrigin.RuleDriven;
    private const ActionOrigin Profile = ActionOrigin.ProfileOrOnRequest;
    private const ActionReversibility Rev = ActionReversibility.Reversible;
    private const ActionReversibility Irr = ActionReversibility.Irreversible;
    private const ActionReversibility None = ActionReversibility.NotApplicable;
    private const OptimizationRisk Safe = OptimizationRisk.Safe;
    private const OptimizationRisk Moderate = OptimizationRisk.Moderate;
    private const OptimizationRisk Risky = OptimizationRisk.Risky;

    public static readonly FrozenDictionary<string, ActionDefinition> All = new ActionDefinition[]
    {
        new(ActionIds.StartupReview, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.DismCleanup, ActionKind.Software, Safe, Irr, Rule),
        new(ActionIds.DiskCleanup, ActionKind.Software, Safe, Irr, Rule),
        new(ActionIds.StorageSense, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.Trim, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.TcpNormal, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.RepairWindows, ActionKind.Software, Safe, Irr, Rule),
        new(ActionIds.PowerPlan, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.PrivacyStandard, ActionKind.Software, Safe, Rev, Profile),
        new(ActionIds.BloatStandard, ActionKind.Software, Safe, Irr, Profile),
        new(ActionIds.DriverGpu, ActionKind.Manual, null, None, Rule),
        new(ActionIds.DriverChipset, ActionKind.Manual, null, None, Profile),
        new(ActionIds.BiosXmp, ActionKind.ManualBios, null, None, Rule),
        new(ActionIds.BiosUma2G, ActionKind.ManualBios, null, None, Rule),
        new(ActionIds.BiosUpdate, ActionKind.ManualBios, null, None, Rule),
        new(ActionIds.HardwareReplaceSsd, ActionKind.Hardware, null, None, Rule),
        new(ActionIds.HardwareRamDual, ActionKind.Hardware, null, None, Rule),
        new(ActionIds.HardwareStorage, ActionKind.Hardware, null, None, Rule),
        new(ActionIds.Backup, ActionKind.Manual, null, None, Rule),
        new(ActionIds.CompactOs, ActionKind.Software, Moderate, Rev, Profile),
        new(ActionIds.DismResetBase, ActionKind.Software, Risky, Irr, Profile),
        new(ActionIds.GameModeAdvanced, ActionKind.Software, Risky, Rev, Rule),
        new(ActionIds.MonitorHz, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.MpoOff, ActionKind.Software, Moderate, Rev, Rule),
        new(ActionIds.MonitorCable, ActionKind.Manual, null, None, Rule),
        new(ActionIds.GpuPreference, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.WingetUpgrade, ActionKind.Software, Safe, Irr, Rule),
        new(ActionIds.DefenderFull, ActionKind.Software, Safe, None, Rule),
        new(ActionIds.DefenderOffline, ActionKind.Software, Safe, None, Profile),
        new(ActionIds.PuaOn, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.BrowserBackground, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.HeavyApps, ActionKind.Informational, null, None, Rule),
        new(ActionIds.BackupSetup, ActionKind.Software, Safe, Rev, Rule),
        new(ActionIds.NicPower, ActionKind.Software, Safe, Rev, Profile),
        new(ActionIds.IndexExclude, ActionKind.Software, Safe, Rev, Profile),
        new(ActionIds.HardwareBattery, ActionKind.Hardware, null, None, Rule),
        new(ActionIds.HardwareClean, ActionKind.Hardware, null, None, Rule),
        new(ActionIds.HardwareNetCable, ActionKind.Hardware, null, None, Rule),
        new(ActionIds.WindowsUpdatePause7, ActionKind.Software, Moderate, Rev, Profile),
        new(ActionIds.DefenderExclusion, ActionKind.Software, Moderate, Rev, Profile),
        new(ActionIds.ServicesManual, ActionKind.Software, Moderate, Rev, Profile),
        new(ActionIds.FastStartupToggle, ActionKind.Software, Moderate, Rev, Profile),
        new(ActionIds.BiosSecureBoot, ActionKind.ManualBios, null, None, Rule),
        new(ActionIds.SecureBootCertificate, ActionKind.Manual, null, None, Rule),
        new(ActionIds.WindowsUpdateRestore, ActionKind.Manual, null, None, Rule),
        new(ActionIds.Retrim, ActionKind.Software, Safe, None, Profile),
        new(ActionIds.DnsReview, ActionKind.Manual, null, None, Rule),
        new(ActionIds.BrowserReview, ActionKind.Manual, null, None, Rule),
        new(ActionIds.DeviceDriver, ActionKind.Manual, null, None, Rule),
        new(ActionIds.DriverWindowsUpdate, ActionKind.Manual, null, None, Rule),
        new(ActionIds.PowerMode, ActionKind.Manual, null, None, Rule),
        new(ActionIds.Runtimes, ActionKind.Manual, null, None, Rule),
        new(ActionIds.ClockSync, ActionKind.Manual, null, None, Rule),
        new(ActionIds.SsdTool, ActionKind.Manual, null, None, Rule),
        new(ActionIds.AntivirusReview, ActionKind.Manual, null, None, Rule),
    }.ToFrozenDictionary(a => a.Id, StringComparer.Ordinal);

    /// <summary>
    ///     Rule-driven actions whose rules arrive in later phases (3, 4 and 6B). The catalog tests
    ///     require this list to shrink as those rules land.
    /// </summary>
    public static readonly FrozenSet<string> PendingRuleActions = Array
        .Empty<string>()
        .ToFrozenSet(StringComparer.Ordinal);

    public static bool Contains(string actionId) => All.ContainsKey(actionId);
}

/// <summary>Rule ids implemented in phase 2 (plan section 7).</summary>
public static class RuleIds
{
    public const string RamXmp = "R-RAM-XMP";
    public const string RamSingle = "R-RAM-SINGLE";
    public const string RamLow = "R-RAM-LOW";
    public const string ApuUma = "R-APU-UMA";
    public const string DiskFree = "R-DISK-FREE";
    public const string SsdHealth = "R-SSD-HEALTH";
    public const string SsdWear = "R-SSD-WEAR";
    public const string HddSystem = "R-HDD-SYSTEM";
    public const string TrimOff = "R-TRIM-OFF";
    public const string StartupMany = "R-STARTUP-MANY";
    public const string BootSlow = "R-BOOT-SLOW";
    public const string WinCorrupt = "R-WIN-CORRUPT";
    public const string TcpAutoTune = "R-TCP-AUTOTUNE";
    public const string PowerSaver = "R-POWER-SAVER";
    public const string DriverGpuOld = "R-DRIVER-GPU-OLD";
    public const string BiosOld = "R-BIOS-OLD";
    public const string Unstable = "R-UNSTABLE";
    public const string GpuResets = "R-GPU-RESETS";
    public const string VbsOn = "R-VBS-ON";
    public const string NotActivated = "R-NOT-ACTIVATED";

    // Phase 6B
    public const string AppsOutdated = "R-APPS-OUTDATED";
    public const string PuaOff = "R-PUA-OFF";
    public const string MonitorHz = "R-MON-HZ";
    public const string MonitorPort = "R-MON-PORT";
    public const string GpuHybrid = "R-GPU-HYBRID";
    public const string Thermal = "R-THERMAL";
    public const string Battery = "R-BATTERY";
    public const string Net100M = "R-NET-100M";
    public const string RamIdleHigh = "R-RAM-IDLE-HIGH";
    public const string BackupNone = "R-BACKUP-NONE";

    // Plan of improvements, block 2B (read-only diagnostics).
    public const string SecurePlatform = "R-SECURE-PLATFORM";
    public const string UefiCa2023 = "R-UEFI-CA2023";
    public const string UpdateHealth = "R-WU-HEALTH";
    public const string RestoreOff = "R-RESTORE-OFF";

    // Plan of improvements, block 3C.
    public const string DnsReview = "R-DNS-REVIEW";
    public const string BrowserExtensions = "R-BROWSER-EXT";
    public const string BrowserSearch = "R-BROWSER-SEARCH";

    // Plan of improvements, block 4A.
    public const string DeviceProblem = "R-DEVICE-PROBLEM";
    public const string DriversUpdate = "R-DRIVERS-WU";
    public const string PowerModeRule = "R-POWER-MODE";

    // Version 1.2.0, extended diagnosis.
    public const string CpuIdleHigh = "R-CPU-IDLE-HIGH";
    public const string RuntimesMissing = "R-RUNTIMES";
    public const string AppCrashes = "R-APP-CRASHES";
    public const string ClockOff = "R-CLOCK-OFF";
    public const string DumpsBig = "R-DUMPS-BIG";
    public const string SsdTool = "R-SSD-TOOL";
    public const string Antivirus = "R-ANTIVIRUS";
    public const string Leak = "R-LEAK";
}
