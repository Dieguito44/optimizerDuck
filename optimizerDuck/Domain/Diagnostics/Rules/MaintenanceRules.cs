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
