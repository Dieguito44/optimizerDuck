using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>
///     R-SECURE-PLATFORM (improvements 2B, F2-11): Secure Boot off is Medium (the PC boots
///     whatever is on the disk); no TPM 2.0 is Low (Windows 11 asks for it, BitLocker and
///     Windows Hello use it). Both are switched on in the firmware: a BIOS step, never software.
/// </summary>
public sealed class SecurePlatformRule : IDiagnosticRule
{
    public string RuleId => RuleIds.SecurePlatform;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BiosSecureBoot];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var hasBoot = snapshot.Os.SecureBoot.TryGet(out var secureBoot);
        var hasTpm = snapshot.Platform.TpmVersion.TryGet(out var tpm);
        if (!hasBoot && !hasTpm)
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Os.SecureBoot.Reason,
                "platform.secure_boot",
                "platform.tpm"
            );

        var evidence = new List<EvidenceFact>();
        var severity = Severity.Info;
        if (hasBoot && !secureBoot)
        {
            evidence.Add(new("platform.secure_boot", Text: "off"));
            severity = Severity.Medium;
        }
        if (hasTpm && tpm < 2)
        {
            evidence.Add(new("platform.tpm", Text: tpm == 0 ? "none" : "1.2"));
            if (severity < Severity.Low)
                severity = Severity.Low;
        }

        string[] missing = hasBoot ? (hasTpm ? [] : ["platform.tpm"]) : ["platform.secure_boot"];
        return evidence.Count > 0
            ? RuleResult.Found(
                new Finding(RuleId, severity, evidence, [ActionIds.BiosSecureBoot]),
                missing
            )
            : RuleResult.Pass(RuleId, missing);
    }
}

/// <summary>
///     R-UEFI-CA2023 (improvements 2B, F2-11): Secure Boot is on but Windows has not put the
///     "Windows UEFI CA 2023" certificate in the firmware yet. The certificates of 2011 expire
///     in 2026; Windows Update installs the new ones by itself. Zentra only reports it and points
///     to Microsoft's article: it never writes Secure Boot variables.
/// </summary>
public sealed class UefiCa2023Rule : IDiagnosticRule
{
    public string RuleId => RuleIds.UefiCa2023;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.SecureBootCertificate];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (snapshot.Os.SecureBoot.TryGet(out var secureBoot) && !secureBoot)
            return RuleResult.NotApplicable(RuleId);

        var capable = snapshot.Platform.UefiCa2023;
        if (!capable.TryGet(out var state))
            return RuleResult.NotMeasurable(RuleId, capable.Reason, "platform.uefi_ca2023");

        return state == 0
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Low,
                    [new("platform.uefi_ca2023", Text: "missing")],
                    [ActionIds.SecureBootCertificate]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>
///     R-WU-HEALTH (improvements 2B, F2-13): Windows Update does not work. A Windows Update
///     service set to Disabled is High (no security patches at all); an update that keeps
///     failing, or no Windows update in 45 days, is Medium. Only updates with a KB number count:
///     Store apps and Defender's definitions say nothing about Windows Update.
/// </summary>
public sealed class UpdateHealthRule : IDiagnosticRule
{
    public const int MaxDaysWithoutUpdate = 45;

    public string RuleId => RuleIds.UpdateHealth;

    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.WindowsUpdateRestore, ActionIds.RepairWindows];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var u = snapshot.Updates;
        var hasServices = u.DisabledServices.TryGet(out var disabled);
        var hasDays = u.DaysSinceSuccess.TryGet(out var days);
        var hasStuck = u.StuckUpdates.TryGet(out var stuck);
        if (!hasServices && !hasDays && !hasStuck)
            return RuleResult.NotMeasurable(
                RuleId,
                u.DaysSinceSuccess.Reason,
                "update.days_since_success",
                "update.stuck",
                "update.disabled_services"
            );

        var evidence = new List<EvidenceFact>();
        var severity = Severity.Info;
        if (hasServices && disabled!.Count > 0)
        {
            evidence.Add(new("update.disabled_services", Text: string.Join(", ", disabled)));
            severity = Severity.High;
        }
        if (hasStuck && stuck > 0)
        {
            evidence.Add(new("update.stuck", stuck));
            if (severity < Severity.Medium)
                severity = Severity.Medium;
        }
        if (hasDays && days >= MaxDaysWithoutUpdate)
        {
            evidence.Add(new("update.days_since_success", days, Unit: "d"));
            if (severity < Severity.Medium)
                severity = Severity.Medium;
        }

        var missing = new List<string>();
        if (!hasServices)
            missing.Add("update.disabled_services");
        if (!hasDays)
            missing.Add("update.days_since_success");
        if (!hasStuck)
            missing.Add("update.stuck");

        return evidence.Count > 0
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    severity,
                    evidence,
                    [ActionIds.WindowsUpdateRestore, ActionIds.RepairWindows]
                ),
                [.. missing]
            )
            : RuleResult.Pass(RuleId, [.. missing]);
    }
}

/// <summary>
///     R-RESTORE-OFF (improvements 2B, F2-14): System Restore is off, so no restore point can be
///     made before the service (Zentra requires one) or by Windows before updates.
/// </summary>
public sealed class RestoreOffRule : IDiagnosticRule
{
    public string RuleId => RuleIds.RestoreOff;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BackupSetup];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var protection = snapshot.Maintenance.SystemProtectionOn;
        if (!protection.TryGet(out var on))
            return RuleResult.NotMeasurable(RuleId, protection.Reason, "backup.system_protection");

        return on
            ? RuleResult.Pass(RuleId)
            : RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("backup.system_protection", Text: "off")],
                    [ActionIds.BackupSetup]
                )
            );
    }
}
