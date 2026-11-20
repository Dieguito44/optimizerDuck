using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Services.System;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>R-DISK-FREE: little free space on C: (High under 10 %, Medium from 10 to 15 %).</summary>
public sealed class DiskFreeRule : IDiagnosticRule
{
    public string RuleId => RuleIds.DiskFree;

    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.DismCleanup, ActionIds.DiskCleanup, ActionIds.StorageSense];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var volume = snapshot.SystemVolume;
        if (!volume.TotalBytes.TryGet(out var total) || total <= 0)
            return RuleResult.NotMeasurable(RuleId, volume.TotalBytes.Reason, "volume.total_bytes");
        if (!volume.FreeBytes.TryGet(out var free))
            return RuleResult.NotMeasurable(RuleId, volume.FreeBytes.Reason, "volume.free_bytes");

        var percent = Math.Round(free * 100m / total, 1);
        if (percent >= 15)
            return RuleResult.Pass(RuleId);

        List<string> actions = [ActionIds.DismCleanup, ActionIds.DiskCleanup];
        if (snapshot.Config.StorageSenseEnabled is not { HasValue: true, ValueOrDefault: true })
            actions.Add(ActionIds.StorageSense);

        return RuleResult.Found(
            new Finding(
                RuleId,
                percent < 10 ? Severity.High : Severity.Medium,
                [
                    new("disk.free_percent", percent, Unit: "%"),
                    new("disk.free_gb", RuleMath.Gb(free), Unit: "GB"),
                ],
                actions
            )
        );
    }
}

/// <summary>
///     R-SSD-HEALTH: a disk Windows reports as not healthy, or with uncorrected errors.
///     Applies to every disk (the plan name says SSD, the risk is the same on an HDD).
/// </summary>
public sealed class SsdHealthRule : IDiagnosticRule
{
    public string RuleId => RuleIds.SsdHealth;

    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.Backup, ActionIds.HardwareReplaceSsd];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (snapshot.Disks.Count == 0)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, "disks");

        var evidence = new List<EvidenceFact>();
        var unmeasured = new List<string>();
        var measuredAny = false;

        foreach (var disk in snapshot.Disks)
        {
            var subject = RuleMath.DiskSubject(disk);
            var hasHealth = disk.Health.TryGet(out var health);
            var hasErrors = disk.UncorrectedErrors.TryGet(out var errors);
            if (!hasHealth && !hasErrors)
            {
                unmeasured.Add($"disk.health:{subject}");
                continue;
            }

            measuredAny = true;
            if (hasHealth && health != DiskHealth.Healthy)
                evidence.Add(new("disk.health", Text: health.ToString(), Subject: subject));
            if (hasErrors && errors > 0)
                evidence.Add(new("disk.uncorrected_errors", errors, Subject: subject));
        }

        if (!measuredAny)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, [.. unmeasured]);

        // Critical when Windows itself calls a disk unhealthy. Uncorrected errors alone are a
        // lifetime counter (one old error stays forever): High, "watch it and back up"
        // (plan F2-07, audit Z-20).
        // Replacing the disk is recommended only then: Windows called both disks of the version 1
        // test PC Healthy, and one old read error had the report telling the client to buy a disk.
        var unhealthy = evidence.Any(e => e.Key == "disk.health");
        var severity = unhealthy ? Severity.Critical : Severity.High;
        return evidence.Count > 0
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    severity,
                    evidence,
                    unhealthy
                        ? [ActionIds.Backup, ActionIds.HardwareReplaceSsd]
                        : [ActionIds.Backup]
                ),
                [.. unmeasured]
            )
            : RuleResult.Pass(RuleId, [.. unmeasured]);
    }
}

/// <summary>
///     R-SSD-WEAR: an SSD at 80 % or more of its rated life. Wear comes from Windows' reliability
///     counter (real on NVMe); otherwise from smartctl's total host writes against the rated TBW
///     in the reference table. A model not in the table is "not measurable", never a guess.
/// </summary>
public sealed class SsdWearRule : IDiagnosticRule
{
    private const int Threshold = 80;

    public string RuleId => RuleIds.SsdWear;

    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.Backup, ActionIds.HardwareReplaceSsd];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var ssds = snapshot
            .Disks.Where(d => d.Kind is StorageMediaType.Ssd or StorageMediaType.Nvme)
            .ToList();
        if (ssds.Count == 0)
            return RuleResult.NotApplicable(RuleId);

        var evidence = new List<EvidenceFact>();
        var unmeasured = new List<string>();
        var measured = 0;
        var worn = false;

        foreach (var disk in ssds)
        {
            var subject = RuleMath.DiskSubject(disk);
            int? usedPercent = disk.WearPercent.TryGet(out var wear) ? wear : null;

            if (
                usedPercent is null
                && disk.BytesWritten.TryGet(out var written)
                && refs.LookupSsdTbw(disk.Model.ValueOrDefault) is { } tbw
            )
                usedPercent = (int)(written * 100 / (tbw.TbwTerabytes * 1_000_000_000_000L));

            if (usedPercent is not { } used)
            {
                unmeasured.Add($"disk.wear_percent:{subject}");
                continue;
            }

            measured++;
            evidence.Add(new("disk.wear_percent", used, Unit: "%", Subject: subject));
            if (used >= Threshold)
                worn = true;
        }

        if (measured == 0)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, [.. unmeasured]);

        return worn
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Critical,
                    evidence.Where(e => e.Number >= Threshold).ToList(),
                    [ActionIds.Backup, ActionIds.HardwareReplaceSsd]
                ),
                [.. unmeasured]
            )
            : RuleResult.Pass(RuleId, [.. unmeasured]);
    }
}

/// <summary>R-HDD-SYSTEM: Windows is installed on a mechanical disk.</summary>
public sealed class HddSystemRule : IDiagnosticRule
{
    public string RuleId => RuleIds.HddSystem;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.HardwareStorage];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var kind = snapshot.SystemVolume.Kind;
        if (kind == StorageMediaType.Unknown)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, "volume.kind");

        return kind == StorageMediaType.Hdd
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.High,
                    [new("disk.kind", Text: "HDD")],
                    [ActionIds.HardwareStorage]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>R-TRIM-OFF: TRIM disabled while an SSD is installed.</summary>
public sealed class TrimOffRule : IDiagnosticRule
{
    public string RuleId => RuleIds.TrimOff;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.Trim];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var hasSsd =
            snapshot.Disks.Any(d => d.Kind is StorageMediaType.Ssd or StorageMediaType.Nvme)
            || snapshot.SystemVolume.Kind is StorageMediaType.Ssd or StorageMediaType.Nvme;
        if (!hasSsd)
            return RuleResult.NotApplicable(RuleId);

        if (!snapshot.Config.TrimEnabled.TryGet(out var enabled))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Config.TrimEnabled.Reason,
                "config.trim"
            );

        return enabled
            ? RuleResult.Pass(RuleId)
            : RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Medium,
                    [new("config.trim", Text: "off")],
                    [ActionIds.Trim]
                )
            );
    }
}
