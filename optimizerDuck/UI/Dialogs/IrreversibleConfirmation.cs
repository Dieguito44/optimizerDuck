using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace optimizerDuck.UI.Dialogs;

/// <summary>
///     Plan of improvements F1-09: the dialog in front of what cannot be undone (ResetBase, removing
///     apps) or is hard to undo (Compact OS). The title says it; the default button is Cancel, so
///     Enter never says yes.
/// </summary>
public static class IrreversibleConfirmation
{
    /// <summary>The title's label: "No reversible" or "Difícil de revertir".</summary>
    public static string Label(OptimizationConfirmation kind) =>
        Loc.Instance[
            kind == OptimizationConfirmation.HardToRevert
                ? "Irreversible.Label.HardToRevert"
                : "Irreversible.Label.Irreversible"
        ];

    /// <summary>True only when the technician pressed the "yes" button.</summary>
    public static async Task<bool> AskAsync(
        IContentDialogService dialogs,
        OptimizationConfirmation kind,
        string what,
        string why
    )
    {
        if (kind == OptimizationConfirmation.None)
            return true;
        var answer = await dialogs.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Irreversible.Title", Label(kind), what],
                Content = why + "\n\n" + Loc.Instance["Irreversible.Question"],
                PrimaryButtonText = Loc.Instance["Irreversible.Yes"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            }
        );
        return answer == ContentDialogResult.Primary;
    }

    /// <summary>The reason for one setting: its own text when there is one, a general one if not.</summary>
    public static string WhyFor(string optimizationKey, OptimizationConfirmation kind)
    {
        var key = $"Irreversible.Why.{optimizationKey}";
        var text = Loc.Instance[key];
        return string.IsNullOrEmpty(text) || text == key
            ? Loc.Instance[$"Irreversible.Why.{kind}"]
            : text;
    }
}
