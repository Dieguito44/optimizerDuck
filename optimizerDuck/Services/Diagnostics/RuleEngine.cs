using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>Runs every rule on a snapshot. One failing rule never hides the others.</summary>
public static class RuleEngine
{
    public static IReadOnlyList<RuleResult> Evaluate(
        DiagnosticSnapshot snapshot,
        ReferenceTables refs,
        IReadOnlyList<IDiagnosticRule> rules,
        ILogger? logger = null
    )
    {
        var results = new List<RuleResult>(rules.Count);
        foreach (var rule in rules)
        {
            RuleResult result;
            try
            {
                result = rule.Evaluate(snapshot, refs);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Diagnostic rule {RuleId} failed", rule.RuleId);
                result = RuleResult.NotMeasurable(rule.RuleId, NotMeasuredReason.QueryFailed);
            }

            // A finding may only carry actions the rule declares and the catalog holds.
            if (
                result.Finding is { } finding
                && finding.ActionIds.Any(id =>
                    !rule.PossibleActionIds.Contains(id) || !ActionCatalog.Contains(id)
                )
            )
            {
                logger?.LogError(
                    "Diagnostic rule {RuleId} returned actions outside its catalog: {Actions}",
                    rule.RuleId,
                    string.Join(", ", finding.ActionIds)
                );
                result = RuleResult.NotMeasurable(rule.RuleId, NotMeasuredReason.QueryFailed);
            }

            results.Add(result);
        }
        return results;
    }
}

/// <summary>How many points one rule took off.</summary>
public sealed record ScoreDeduction(string RuleId, Severity Severity, int Points);

/// <summary>The score and what took points off, for the page and the PDF.</summary>
public sealed record ScoreBreakdown(
    int Score,
    int RawDeduction,
    IReadOnlyList<ScoreDeduction> Deductions,
    int RulesEvaluated,
    int RulesNotMeasurable
);

/// <summary>Plan section 7.1: start at 100, Critical -25, High -12, Medium -6, Low -2, minimum 0.</summary>
public static class ScoreCalculator
{
    public static int PointsFor(Severity severity) =>
        severity switch
        {
            Severity.Critical => 25,
            Severity.High => 12,
            Severity.Medium => 6,
            Severity.Low => 2,
            _ => 0,
        };

    public static ScoreBreakdown Calculate(IReadOnlyList<RuleResult> results)
    {
        var deductions = results
            .Where(r => r.Finding is not null)
            .Select(r => new ScoreDeduction(
                r.RuleId,
                r.Finding!.Severity,
                PointsFor(r.Finding.Severity)
            ))
            .ToList();

        var raw = deductions.Sum(d => d.Points);
        return new ScoreBreakdown(
            Math.Max(0, 100 - raw),
            raw,
            deductions,
            results.Count(r => r.Outcome is RuleOutcome.Pass or RuleOutcome.Finding),
            results.Count(r => r.Outcome == RuleOutcome.NotMeasurable)
        );
    }
}

/// <summary>A complete diagnosis: what was measured, what the rules found and the score.</summary>
public sealed record DiagnosticReport(
    DiagnosticSnapshot Snapshot,
    IReadOnlyList<RuleResult> Results,
    ScoreBreakdown Score,
    string RamKitsUpdated,
    string SsdTbwUpdated,
    string BiosLatestUpdated
)
{
    public IEnumerable<Finding> Findings =>
        Results
            .Where(r => r.Finding is not null)
            .Select(r => r.Finding!)
            .OrderByDescending(f => f.Severity);

    public static DiagnosticReport Create(
        DiagnosticSnapshot snapshot,
        ReferenceTables refs,
        IReadOnlyList<IDiagnosticRule> rules,
        ILogger? logger = null
    )
    {
        var results = RuleEngine.Evaluate(snapshot, refs, rules, logger);
        return new DiagnosticReport(
            snapshot,
            results,
            ScoreCalculator.Calculate(results),
            refs.RamKitsUpdated,
            refs.SsdTbwUpdated,
            refs.BiosLatestUpdated
        );
    }
}
