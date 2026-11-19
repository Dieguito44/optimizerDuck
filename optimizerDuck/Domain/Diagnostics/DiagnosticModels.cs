namespace optimizerDuck.Domain.Diagnostics;

/// <summary>Finding severity (plan section 7). Info is listed but never lowers the score.</summary>
public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>What a rule concluded.</summary>
public enum RuleOutcome
{
    /// <summary>Measured and nothing wrong.</summary>
    Pass,

    /// <summary>Measured and something is wrong.</summary>
    Finding,

    /// <summary>A required input could not be measured. Never treated as a pass or a finding.</summary>
    NotMeasurable,

    /// <summary>The rule does not apply to this machine (e.g. a desktop-only rule on a laptop).</summary>
    NotApplicable,
}

/// <summary>
///     One measured fact behind a finding. <see cref="Number" /> carries every figure the report may
///     quote, so the AI validator (phase 5) can check that each number in the text exists here.
/// </summary>
/// <param name="Key">Stable evidence key, e.g. "ram.configured_mts".</param>
/// <param name="Number">The figure, when the fact is numeric.</param>
/// <param name="Text">The value, when the fact is text (a model, a version, a state).</param>
/// <param name="Unit">Unit of <see cref="Number" />, e.g. "MT/s", "%", "MB".</param>
/// <param name="Subject">What the fact is about when there are several, e.g. a disk model.</param>
public sealed record EvidenceFact(
    string Key,
    decimal? Number = null,
    string? Text = null,
    string? Unit = null,
    string? Subject = null
);

/// <summary>A problem a rule found, with its evidence and the catalog actions that address it.</summary>
public sealed record Finding(
    string RuleId,
    Severity Severity,
    IReadOnlyList<EvidenceFact> Evidence,
    IReadOnlyList<string> ActionIds
);

/// <summary>The result of evaluating one rule.</summary>
public sealed record RuleResult(
    string RuleId,
    RuleOutcome Outcome,
    Finding? Finding,
    NotMeasuredReason? Reason,
    IReadOnlyList<string> MissingInputs
)
{
    public static RuleResult Pass(string ruleId, params string[] partiallyMissingInputs) =>
        new(ruleId, RuleOutcome.Pass, null, null, partiallyMissingInputs);

    public static RuleResult Found(Finding finding, params string[] partiallyMissingInputs) =>
        new(finding.RuleId, RuleOutcome.Finding, finding, null, partiallyMissingInputs);

    public static RuleResult NotMeasurable(
        string ruleId,
        NotMeasuredReason reason,
        params string[] missingInputs
    ) => new(ruleId, RuleOutcome.NotMeasurable, null, reason, missingInputs);

    public static RuleResult NotApplicable(string ruleId) =>
        new(ruleId, RuleOutcome.NotApplicable, null, NotMeasuredReason.NotApplicable, []);
}

/// <summary>
///     A diagnostic rule. Rules are pure: they read only the snapshot and the reference tables,
///     never the clock, the registry, WMI or localized text.
/// </summary>
public interface IDiagnosticRule
{
    /// <summary>The rule id from plan section 7, e.g. "R-RAM-XMP".</summary>
    string RuleId { get; }

    /// <summary>Every catalog action a finding of this rule may carry.</summary>
    IReadOnlyList<string> PossibleActionIds { get; }

    RuleResult Evaluate(DiagnosticSnapshot snapshot, Reference.ReferenceTables refs);
}
