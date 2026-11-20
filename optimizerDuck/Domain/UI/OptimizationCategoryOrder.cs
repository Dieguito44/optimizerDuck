namespace optimizerDuck.Domain.UI;

/// <summary>
///     Specifies the display order of optimization categories in the UI.
/// </summary>
public enum OptimizationCategoryOrder
{
    Performance,

    SecurityAndPrivacy,

    Gpu,

    Power,

    BloatwareAndServices,

    UserExperience,

    AI,

    /// <summary>Zentra: component store, Storage Sense, TRIM, Compact OS.</summary>
    Storage,

    /// <summary>Zentra: what Diego took out of the blacklist (20/11/2026), all Risky.</summary>
    Expert,
}
