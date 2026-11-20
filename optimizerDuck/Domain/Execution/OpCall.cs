using Microsoft.Extensions.Logging;

namespace optimizerDuck.Domain.Execution;

/// <summary>
///     Explicit per-operation call context: every dependency travels as a parameter.
/// </summary>
public class OpCall
{
    /// <summary>Gets the collector this operation records its changes into.</summary>
    public ChangeSet Changes { get; init; } = new();

    /// <summary>Gets the logger the operation writes its diagnostics to.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Gets the token that cancels the operation.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    ///     When set, this call is a dry run (plan F2-03): the primitives that change the system
    ///     describe their change in the plan instead of making it.
    /// </summary>
    public PreviewPlan? Preview { get; init; }
}
