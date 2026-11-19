using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>R-APPS-OUTDATED: winget has a newer version for 5 or more programs.</summary>
public sealed class AppsOutdatedRule : IDiagnosticRule
{
    public const int Threshold = 5;

    public string RuleId => RuleIds.AppsOutdated;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.WingetUpgrade];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var upgrades = snapshot.Maintenance.WingetUpgrades;
        if (!upgrades.TryGet(out var count))
            return RuleResult.NotMeasurable(RuleId, upgrades.Reason, "apps.outdated");

        return count >= Threshold
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("apps.outdated", count)],
                    [ActionIds.WingetUpgrade]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>
///     R-PUA-OFF: Defender does not block potentially unwanted apps. The plan names value 0
///     (off); audit mode (2) is a finding too, because it only logs and blocks nothing (Diego's
///     PC was in audit mode, 19/11/2026). Not applicable when another antivirus is active.
/// </summary>
public sealed class PuaOffRule : IDiagnosticRule
{
    public string RuleId => RuleIds.PuaOff;
    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.PuaOn, ActionIds.DefenderFull];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var pua = snapshot.Maintenance.PuaProtection;
        if (!pua.TryGet(out var mode))
            return pua.Reason == NotMeasuredReason.NotApplicable
                ? RuleResult.NotApplicable(RuleId)
                : RuleResult.NotMeasurable(RuleId, pua.Reason, "defender.pua");

        return mode == PuaMode.On
            ? RuleResult.Pass(RuleId)
            : RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("defender.pua", Text: mode == PuaMode.Audit ? "audit" : "off")],
                    [ActionIds.PuaOn, ActionIds.DefenderFull]
                )
            );
    }
}

/// <summary>R-BATTERY: the battery holds less than 60 % of its design capacity (laptops).</summary>
public sealed class BatteryRule : IDiagnosticRule
{
    public const int MinPercent = 60;

    public string RuleId => RuleIds.Battery;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.HardwareBattery];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var battery = snapshot.Maintenance.BatteryCapacityPercent;
        if (!battery.TryGet(out var percent))
            return battery.Reason == NotMeasuredReason.NotApplicable
                ? RuleResult.NotApplicable(RuleId)
                : RuleResult.NotMeasurable(RuleId, battery.Reason, "battery.capacity");

        return percent < MinPercent
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.High,
                    [new("battery.capacity", percent, Unit: "%")],
                    [ActionIds.HardwareBattery]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-NET-100M: the wired connection runs at 100 Mb/s on an adapter that does 1 Gb/s.</summary>
public sealed class Net100MRule : IDiagnosticRule
{
    public string RuleId => RuleIds.Net100M;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.HardwareNetCable];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var m = snapshot.Maintenance;
        if (!m.WiredLinkMbps.TryGet(out var link))
            return m.WiredLinkMbps.Reason == NotMeasuredReason.NotApplicable
                ? RuleResult.NotApplicable(RuleId)
                : RuleResult.NotMeasurable(RuleId, m.WiredLinkMbps.Reason, "network.link");
        if (!m.WiredMaxMbps.TryGet(out var max))
            return RuleResult.NotMeasurable(RuleId, m.WiredMaxMbps.Reason, "network.max");

        return link <= 100 && max >= 1000
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [
                        new("network.link", link, Unit: "Mb/s"),
                        new("network.max", max, Unit: "Mb/s"),
                    ],
                    [ActionIds.HardwareNetCable]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-RAM-IDLE-HIGH: more than 80 % of the RAM in use at rest (from a session measurement).</summary>
public sealed class RamIdleHighRule : IDiagnosticRule
{
    public const double MaxPercent = 80;

    public string RuleId => RuleIds.RamIdleHigh;
    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.StartupReview, ActionIds.BrowserBackground, ActionIds.HardwareRamDual];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var idle = snapshot.Maintenance.IdleRamPercent;
        if (!idle.TryGet(out var percent))
            return RuleResult.NotMeasurable(RuleId, idle.Reason, "ram.idle_percent");

        return percent > MaxPercent
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.High,
                    [new("ram.idle_percent", Math.Round((decimal)percent, 1), Unit: "%")],
                    [
                        ActionIds.StartupReview,
                        ActionIds.BrowserBackground,
                        ActionIds.HardwareRamDual,
                    ]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-BACKUP-NONE: no restore points and no OneDrive folder on the PC.</summary>
public sealed class BackupNoneRule : IDiagnosticRule
{
    public string RuleId => RuleIds.BackupNone;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BackupSetup];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var m = snapshot.Maintenance;
        if (!m.RestorePoints.TryGet(out var points))
            return RuleResult.NotMeasurable(
                RuleId,
                m.RestorePoints.Reason,
                "backup.restore_points"
            );
        if (!m.OneDriveConfigured.TryGet(out var oneDrive))
            return RuleResult.NotMeasurable(RuleId, m.OneDriveConfigured.Reason, "backup.onedrive");

        return points == 0 && !oneDrive
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("backup.restore_points", 0), new("backup.onedrive", Text: "no")],
                    [ActionIds.BackupSetup]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}
