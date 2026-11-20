namespace optimizerDuck.Domain.UI;

/// <summary>
///     Plan of improvements F1-09 (audit Z-06 and Z-07): settings that only run after the
///     technician says yes in a dialog that names what cannot be undone. Without that yes the
///     engine refuses them (<c>OptimizationService.ApplyAsync</c>), and a preset never holds one.
/// </summary>
public enum OptimizationConfirmation
{
    /// <summary>Applied like any other setting.</summary>
    None,

    /// <summary>"No reversible": Zentra cannot undo it (ResetBase).</summary>
    Irreversible,

    /// <summary>Undone, but slowly and only with free space (Compact OS).</summary>
    HardToRevert,
}
