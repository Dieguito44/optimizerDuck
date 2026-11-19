using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Services.System;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>R-STARTUP-MANY: more than 8 apps start with Windows.</summary>
public sealed class StartupManyRule : IDiagnosticRule
{
    private const int Maximum = 8;

    public string RuleId => RuleIds.StartupMany;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.StartupReview];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Config.StartupAppsEnabled.TryGet(out var count))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Config.StartupAppsEnabled.Reason,
                "config.startup_apps"
            );

        return count > Maximum
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("config.startup_apps", count)],
                    [ActionIds.StartupReview]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-BOOT-SLOW: the median of the last 3 boots is over 60 seconds.</summary>
public sealed class BootSlowRule : IDiagnosticRule
{
    private const int MaxMs = 60_000;

    public string RuleId => RuleIds.BootSlow;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.StartupReview];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.BootMedianMs.TryGet(out var ms))
            return RuleResult.NotMeasurable(RuleId, snapshot.BootMedianMs.Reason, "boot.median");

        return ms > MaxMs
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("boot.median_s", Math.Round(ms / 1000m, 1), Unit: "s")],
                    [ActionIds.StartupReview]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-WIN-CORRUPT: the Windows component store is damaged (DISM CheckHealth).</summary>
public sealed class WinCorruptRule : IDiagnosticRule
{
    public string RuleId => RuleIds.WinCorrupt;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.RepairWindows];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.ImageHealth.TryGet(out var health))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.ImageHealth.Reason,
                "windows.image_health"
            );

        return health == Services.Repair.WindowsImageHealth.Healthy
            ? RuleResult.Pass(RuleId)
            : RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.High,
                    [new("windows.image_health", Text: health.ToString())],
                    [ActionIds.RepairWindows]
                )
            );
    }
}

/// <summary>R-TCP-AUTOTUNE: TCP receive window auto-tuning disabled.</summary>
public sealed class TcpAutoTuneRule : IDiagnosticRule
{
    public string RuleId => RuleIds.TcpAutoTune;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.TcpNormal];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Config.TcpAutoTuning.TryGet(out var level))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Config.TcpAutoTuning.Reason,
                "config.tcp_autotuning"
            );

        return level == TcpAutoTuning.Disabled
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("config.tcp_autotuning", Text: level.ToString())],
                    [ActionIds.TcpNormal]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-POWER-SAVER: the Power saver plan is active on a desktop.</summary>
public sealed class PowerSaverRule : IDiagnosticRule
{
    public string RuleId => RuleIds.PowerSaver;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.PowerPlan];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Os.DeviceKind.TryGet(out var kind))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Os.DeviceKind.Reason,
                "os.device_kind"
            );
        if (kind != DeviceKind.Desktop)
            return RuleResult.NotApplicable(RuleId);

        if (!snapshot.Config.PowerPlan.TryGet(out var plan))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Config.PowerPlan.Reason,
                "config.power_plan"
            );

        return plan == PowerPlanKind.PowerSaver
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("config.power_plan", Text: plan.ToString())],
                    [ActionIds.PowerPlan]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-DRIVER-GPU-OLD: a graphics driver more than 12 months old.</summary>
public sealed class DriverGpuOldRule : IDiagnosticRule
{
    private const int MaxAgeMonths = 12;

    public string RuleId => RuleIds.DriverGpuOld;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.DriverGpu];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (snapshot.Gpus.Count == 0)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, "gpus");

        var today = DateOnly.FromDateTime(snapshot.CapturedAtUtc.UtcDateTime);
        var evidence = new List<EvidenceFact>();
        var unmeasured = new List<string>();
        var measured = 0;

        foreach (var gpu in snapshot.Gpus)
        {
            var subject = gpu.Model.ValueOrDefault ?? gpu.Vendor.ToString();
            if (!gpu.DriverDate.TryGet(out var date))
            {
                unmeasured.Add($"gpu.driver_date:{subject}");
                continue;
            }

            measured++;
            var months = (today.Year - date.Year) * 12 + today.Month - date.Month;
            if (today.Day < date.Day)
                months--;
            if (months > MaxAgeMonths)
            {
                evidence.Add(
                    new("gpu.driver_age_months", months, Unit: "months", Subject: subject)
                );
                evidence.Add(
                    new("gpu.driver_date", Text: date.ToString("yyyy-MM-dd"), Subject: subject)
                );
            }
        }

        if (measured == 0)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, [.. unmeasured]);

        return evidence.Count > 0
            ? RuleResult.Found(
                new Finding(RuleId, Severity.Medium, evidence, [ActionIds.DriverGpu]),
                [.. unmeasured]
            )
            : RuleResult.Pass(RuleId, [.. unmeasured]);
    }
}

/// <summary>R-BIOS-OLD: the BIOS is older than the latest stable one in bios_latest.json.</summary>
public sealed class BiosOldRule : IDiagnosticRule
{
    public string RuleId => RuleIds.BiosOld;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BiosUpdate];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var board = snapshot.Board;
        if (!board.BiosVersion.TryGet(out var installed))
            return RuleResult.NotMeasurable(RuleId, board.BiosVersion.Reason, "bios.version");

        var latest = refs.LookupBios(
            board.Manufacturer.ValueOrDefault,
            board.Product.ValueOrDefault
        );
        if (latest is null)
            return RuleResult.NotMeasurable(
                RuleId,
                NotMeasuredReason.NotInReferenceTable,
                "bios.latest"
            );

        if (string.Equals(installed, latest.Version, StringComparison.OrdinalIgnoreCase))
            return RuleResult.Pass(RuleId);

        if (!board.BiosDate.TryGet(out var installedDate))
            return RuleResult.NotMeasurable(RuleId, board.BiosDate.Reason, "bios.date");

        // A newer installed date means a beta or a version newer than the table: not "old".
        if (installedDate >= latest.Date)
            return RuleResult.Pass(RuleId);

        return RuleResult.Found(
            new Finding(
                RuleId,
                Severity.Low,
                [new("bios.installed", Text: installed), new("bios.latest", Text: latest.Version)],
                [ActionIds.BiosUpdate]
            )
        );
    }
}

/// <summary>
///     R-UNSTABLE: more than 2 unexpected shutdowns (Kernel-Power 41) in 30 days, not counting
///     ones caused by holding the power button. A power cut also logs this event.
/// </summary>
public sealed class UnstableRule : IDiagnosticRule
{
    private const int Maximum = 2;

    public string RuleId => RuleIds.Unstable;

    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BiosXmp, ActionIds.Backup];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var stability = snapshot.Stability;
        if (!stability.Kp41Count30d.TryGet(out var total))
            return RuleResult.NotMeasurable(
                RuleId,
                stability.Kp41Count30d.Reason,
                "stability.kp41"
            );

        var button = stability.Kp41PowerButton30d.TryGet(out var b) ? b : 0;
        var unexpected = Math.Max(0, total - button);
        if (unexpected <= Maximum)
            return RuleResult.Pass(RuleId);

        var evidence = new List<EvidenceFact> { new("stability.unexpected_shutdowns", unexpected) };
        if (stability.BugChecks30d.TryGet(out var bugChecks))
            evidence.Add(new("stability.bugchecks", bugChecks));

        return RuleResult.Found(
            new Finding(RuleId, Severity.High, evidence, [ActionIds.BiosXmp, ActionIds.Backup])
        );
    }
}

/// <summary>R-VBS-ON: VBS or Memory Integrity is on. Information only.</summary>
public sealed class VbsOnRule : IDiagnosticRule
{
    public string RuleId => RuleIds.VbsOn;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.GameModeAdvanced];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var hasVbs = snapshot.Os.Vbs.TryGet(out var vbs);
        var hasHvci = snapshot.Os.Hvci.TryGet(out var hvci);
        if (!hasVbs && !hasHvci)
            return RuleResult.NotMeasurable(RuleId, snapshot.Os.Vbs.Reason, "os.vbs", "os.hvci");

        if (!vbs && !hvci)
            return RuleResult.Pass(RuleId);

        return RuleResult.Found(
            new Finding(
                RuleId,
                Severity.Info,
                [
                    new("os.vbs", Text: hasVbs ? (vbs ? "on" : "off") : "unknown"),
                    new("os.hvci", Text: hasHvci ? (hvci ? "on" : "off") : "unknown"),
                ],
                [ActionIds.GameModeAdvanced]
            )
        );
    }
}

/// <summary>R-NOT-ACTIVATED: Windows is not activated. Only reported, never activated.</summary>
public sealed class NotActivatedRule : IDiagnosticRule
{
    public string RuleId => RuleIds.NotActivated;
    public IReadOnlyList<string> PossibleActionIds { get; } = [];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Os.Activated.TryGet(out var activated))
            return RuleResult.NotMeasurable(RuleId, snapshot.Os.Activated.Reason, "os.activated");

        return activated
            ? RuleResult.Pass(RuleId)
            : RuleResult.Found(
                new Finding(RuleId, Severity.Info, [new("os.activated", Text: "no")], [])
            );
    }
}

/// <summary>The rules evaluated in phase 2, in report order.</summary>
public static class DiagnosticRules
{
    public static IReadOnlyList<IDiagnosticRule> All { get; } =
    [
        new SsdHealthRule(),
        new SsdWearRule(),
        new RamSingleRule(),
        new RamLowRule(),
        new RamXmpRule(),
        new ApuUmaRule(),
        new HddSystemRule(),
        new DiskFreeRule(),
        new UnstableRule(),
        new WinCorruptRule(),
        new TrimOffRule(),
        new StartupManyRule(),
        new BootSlowRule(),
        new TcpAutoTuneRule(),
        new PowerSaverRule(),
        new DriverGpuOldRule(),
        new BiosOldRule(),
        new VbsOnRule(),
        new NotActivatedRule(),
    ];
}
