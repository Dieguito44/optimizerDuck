using System.Globalization;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.Configuration;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>A finding ready to show: localized title, description, evidence and actions.</summary>
public sealed record FindingView(
    string RuleId,
    Severity Severity,
    string SeverityText,
    int Points,
    string Title,
    string Description,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Actions
);

/// <summary>A rule that could not be measured, with the reason in plain words.</summary>
public sealed record NotMeasuredView(string RuleId, string Title, string Reason);

/// <summary>
///     Turns findings into localized text. Rules never produce text themselves, so the same
///     finding renders in Spanish for the client and feeds structured evidence to the AI.
/// </summary>
public static class FindingTextRenderer
{
    public static FindingView Render(Finding finding) =>
        new(
            finding.RuleId,
            finding.Severity,
            SeverityText(finding.Severity),
            ScoreCalculator.PointsFor(finding.Severity),
            Loc.Instance[$"Diagnostic.Rule.{finding.RuleId}.Title"],
            Loc.Instance[$"Diagnostic.Rule.{finding.RuleId}.Description"],
            finding.Evidence.Select(EvidenceLine).ToList(),
            finding
                .ActionIds.Where(ActionCatalog.Contains)
                .Select(id => Loc.Instance[ActionCatalog.All[id].TitleKey])
                .ToList()
        );

    public static NotMeasuredView RenderNotMeasured(RuleResult result) =>
        new(
            result.RuleId,
            Loc.Instance[$"Diagnostic.Rule.{result.RuleId}.Title"],
            Loc.Instance[$"Diagnostic.Reason.{result.Reason ?? NotMeasuredReason.NotReported}"]
        );

    public static string SeverityText(Severity severity) =>
        Loc.Instance[$"Diagnostic.Severity.{severity}"];

    /// <summary>"Wear: 85 % (KINGSTON SA400S37/480GB)".</summary>
    public static string EvidenceLine(EvidenceFact fact)
    {
        var label = Loc.Instance[$"Diagnostic.Evidence.{fact.Key}"];
        string value;
        if (fact.Number is { } number)
        {
            value = number.ToString("0.#", CultureInfo.CurrentCulture);
            if (!string.IsNullOrEmpty(fact.Unit))
                value += fact.Unit == "%" ? " %" : $" {UnitText(fact.Unit)}";
            if (!string.IsNullOrEmpty(fact.Text))
                value += $" ({ValueText(fact.Text)})";
        }
        else
        {
            value = ValueText(fact.Text ?? string.Empty);
        }

        var line = $"{label}: {value}";
        return string.IsNullOrEmpty(fact.Subject) ? line : $"{line} · {fact.Subject}";
    }

    private static string UnitText(string unit)
    {
        var key = $"Diagnostic.Unit.{unit}";
        var text = Loc.Instance[key];
        return text == key ? unit : text;
    }

    /// <summary>Translates known evidence words ("on", "table"); anything else is shown as is.</summary>
    private static string ValueText(string text)
    {
        var key = $"Diagnostic.Value.{text}";
        var translated = Loc.Instance[key];
        return translated == key ? text : translated;
    }
}
