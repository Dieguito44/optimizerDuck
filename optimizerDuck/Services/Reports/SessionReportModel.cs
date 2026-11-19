using System.Globalization;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;

namespace optimizerDuck.Services.Reports;

/// <summary>Which direction is an improvement for a metric.</summary>
public enum Better
{
    Lower,
    Higher,
}

/// <summary>One row of the before/after table, already formatted.</summary>
public sealed record MetricRow(
    string Name,
    string Before,
    string After,
    string Difference,
    bool? Improved
);

/// <summary>One finding block of the report.</summary>
public sealed record ReportFinding(
    string Severity,
    int Points,
    string Title,
    string Description,
    IReadOnlyList<string> Evidence
);

/// <summary>An action Zentra applied during the session (from the optimizerDuck history).</summary>
public sealed record AppliedAction(string Name, DateTime AppliedAt, int Changes);

/// <summary>The written part of the report (AI or template), ready to print.</summary>
public sealed record WrittenReport(
    string Summary,
    IReadOnlyList<WrittenPriority> Priorities,
    IReadOnlyList<string> Warnings,
    string Source
)
{
    public static WrittenReport? From(Ai.AiReportRecord? record)
    {
        if (record is null)
            return null;
        var actions = record
            .Payload.Findings.SelectMany(f => f.Actions)
            .GroupBy(a => a.ActionId)
            .ToDictionary(g => g.Key, g => g.First());
        return new WrittenReport(
            record.Report.Summary,
            record
                .Report.Priorities.OrderBy(p => p.Priority)
                .Select(p =>
                    actions.TryGetValue(p.ActionId, out var a)
                        ? new WrittenPriority(p.Priority, a.Title, a.Who, p.Explanation)
                        : null
                )
                .OfType<WrittenPriority>()
                .ToList(),
            record.Report.Warnings,
            record.Source == Ai.ReportSource.Ai
                ? Loc.Instance["Report.Written.Source.Ai"]
                : Loc.Instance["Report.Written.Source.Template"]
        );
    }
}

public sealed record WrittenPriority(int Number, string Title, string Who, string Explanation);

/// <summary>
///     Everything the PDF shows, already in the report language. Pure: built from a session and
///     the change history, no I/O, so it can be tested without drawing a PDF.
/// </summary>
public sealed record SessionReportModel
{
    public required string Title { get; init; }
    public string? ClientLabel { get; init; }
    public required string Date { get; init; }
    public int? ScoreBefore { get; init; }
    public int? ScoreAfter { get; init; }
    public required IReadOnlyList<MetricRow> Metrics { get; init; }
    public required IReadOnlyList<ReportFinding> Findings { get; init; }
    public required IReadOnlyList<string> NotMeasured { get; init; }
    public required IReadOnlyList<AppliedAction> Applied { get; init; }

    /// <summary>What the client must do: BIOS, hardware and manual actions from the findings.</summary>
    public required IReadOnlyList<string> ClientPending { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
    public IReadOnlyList<BootCulprit> BootCulprits { get; init; } = [];
    public WrittenReport? Written { get; init; }

    public static SessionReportModel Build(
        ServiceSession session,
        IEnumerable<ChangeRecord> history,
        Func<Guid, string?> optimizationName,
        DateTimeOffset now,
        CultureInfo culture
    )
    {
        var latest = session.DiagnosisAfter ?? session.DiagnosisBefore;
        var findings =
            latest
                ?.Findings.OrderByDescending(f => f.Severity)
                .Select(f =>
                {
                    var view = FindingTextRenderer.Render(f);
                    return new ReportFinding(
                        view.SeverityText,
                        view.Points,
                        view.Title,
                        view.Description,
                        view.Evidence
                    );
                })
                .ToList()
            ?? [];

        var pending =
            latest
                ?.Findings.OrderByDescending(f => f.Severity)
                .SelectMany(f => f.ActionIds.Select(id => (f.RuleId, ActionId: id)))
                .Where(x =>
                    ActionCatalog.All.TryGetValue(x.ActionId, out var action)
                    && action.Kind
                        is ActionKind.ManualBios
                            or ActionKind.Hardware
                            or ActionKind.Manual
                )
                .Select(x => FindingTextRenderer.ActionTitle(x.RuleId, x.ActionId))
                .Distinct()
                .ToList()
            ?? [];

        var applied = history
            .Where(r =>
                r.Operation == ChangeRecordOperation.Apply
                && r.AppliedAt.ToUniversalTime() >= session.CreatedAtUtc.UtcDateTime
                && r.AppliedAt.ToUniversalTime() <= now.UtcDateTime
                && r.ChangedCount + r.CountOf(ChangeKind.Skip) > 0
            )
            .OrderBy(r => r.AppliedAt)
            .Select(r => new AppliedAction(
                optimizationName(r.Id) ?? r.LogName,
                r.AppliedAt.ToLocalTime(),
                r.ChangedCount
            ))
            .ToList();

        var notes = new List<string> { Loc.Instance["Report.Note.Reversible"] };
        if (
            session.Before?.FastStartupEnabled is { HasValue: true } b
            && session.After?.FastStartupEnabled is { HasValue: true } a
            && b.ValueOrDefault != a.ValueOrDefault
        )
            notes.Add(Loc.Instance["Report.Note.FastStartupChanged"]);
        if (
            session.Before?.BootSource
                is { HasValue: true, ValueOrDefault: BootTimeSource.KernelToLogon }
            || session.After?.BootSource
                is { HasValue: true, ValueOrDefault: BootTimeSource.KernelToLogon }
        )
            notes.Add(Loc.Instance["Report.Note.BootKernelToLogon"]);
        notes.Add(Loc.Instance["Report.Note.NotMeasured"]);

        return new SessionReportModel
        {
            Title = Loc.Instance["Report.Title"],
            ClientLabel = string.IsNullOrWhiteSpace(session.ClientLabel)
                ? null
                : session.ClientLabel.Trim(),
            Date = now.ToLocalTime().ToString("f", culture),
            ScoreBefore = session.DiagnosisBefore?.Score.Score,
            ScoreAfter = session.DiagnosisAfter?.Score.Score,
            Metrics = BuildMetrics(session.Before, session.After, culture),
            Findings = findings,
            NotMeasured =
                latest
                    ?.NotMeasurable.Select(r =>
                    {
                        var view = FindingTextRenderer.RenderNotMeasured(r);
                        return $"{view.Title}: {view.Reason}";
                    })
                    .ToList()
                ?? [],
            Applied = applied,
            ClientPending = pending,
            Notes = notes,
            BootCulprits = (session.After ?? session.Before)?.BootCulprits ?? [],
            Written = WrittenReport.From(session.Report),
        };
    }

    public static IReadOnlyList<MetricRow> BuildMetrics(
        Measurement? before,
        Measurement? after,
        CultureInfo culture
    )
    {
        const double GiB = 1024d * 1024 * 1024;
        const double MiB = 1024d * 1024;

        return
        [
            Row(
                "Report.Metric.Boot",
                before?.BootMedianMs,
                after?.BootMedianMs,
                ms => ms / 1000.0,
                "0.0",
                " s",
                Better.Lower,
                culture
            ),
            Row(
                "Report.Metric.IdleRam",
                before?.IdleRamUsedBytes,
                after?.IdleRamUsedBytes,
                b => b / MiB,
                "#,##0",
                " MB",
                Better.Lower,
                culture
            ),
            Row(
                "Report.Metric.FreeSpace",
                before?.SystemFreeBytes,
                after?.SystemFreeBytes,
                b => b / GiB,
                "#,##0.0",
                " GB",
                Better.Higher,
                culture
            ),
            Row(
                "Report.Metric.StartupApps",
                before?.StartupAppsEnabled,
                after?.StartupAppsEnabled,
                v => v,
                "#,##0",
                "",
                Better.Lower,
                culture
            ),
            Row(
                "Report.Metric.Processes",
                before?.ProcessCount,
                after?.ProcessCount,
                v => v,
                "#,##0",
                "",
                Better.Lower,
                culture
            ),
            Row(
                "Report.Metric.Services",
                before?.RunningServices,
                after?.RunningServices,
                v => v,
                "#,##0",
                "",
                Better.Lower,
                culture
            ),
            Row(
                "Report.Metric.SequentialRead",
                before?.SequentialReadMiBps,
                after?.SequentialReadMiBps,
                v => v,
                "#,##0",
                " MB/s",
                Better.Higher,
                culture
            ),
            Row(
                "Report.Metric.RandomRead",
                before?.RandomReadIops,
                after?.RandomReadIops,
                v => v,
                "#,##0",
                " IOPS",
                Better.Higher,
                culture
            ),
        ];
    }

    private static MetricRow Row<T>(
        string nameKey,
        Measured<T>? before,
        Measured<T>? after,
        Func<T, double> toDisplay,
        string format,
        string unit,
        Better better,
        CultureInfo culture
    )
    {
        double? b = before is { HasValue: true } mb ? toDisplay(mb.ValueOrDefault!) : null;
        double? a = after is { HasValue: true } ma ? toDisplay(ma.ValueOrDefault!) : null;

        string Show(double? value, Measured<T>? measured) =>
            value is { } v ? v.ToString(format, culture) + unit
            : measured is null ? "—"
            : Loc.Instance["Report.NotMeasured"];

        string difference = "—";
        bool? improved = null;
        if (b is { } bv && a is { } av)
        {
            var delta = av - bv;
            var rounded = Math.Round(delta, format.Contains('.') ? 1 : 0);
            difference = (rounded > 0 ? "+" : "") + rounded.ToString(format, culture) + unit;
            if (rounded != 0)
                improved = better == Better.Lower ? rounded < 0 : rounded > 0;
        }

        return new MetricRow(
            Loc.Instance[nameKey],
            Show(b, before),
            Show(a, after),
            difference,
            improved
        );
    }
}
