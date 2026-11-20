using optimizerDuck.Domain.UI;
using optimizerDuck.UI.Dialogs;
using Wpf.Ui.Controls;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>Found in the Sandbox tests (19/11/2026): Enter pressed "yes" in the "No reversible" dialog.</summary>
public class IrreversibleDialogTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    [Theory]
    [InlineData(OptimizationConfirmation.Irreversible)]
    [InlineData(OptimizationConfirmation.HardToRevert)]
    public void CancelIsTheDefault_AndYesIsRed(OptimizationConfirmation kind)
    {
        OnSta(() =>
        {
            var dialog = IrreversibleConfirmation.Build(kind, "ResetBase", "Porque sí.");
            Assert.Equal(ContentDialogButton.Close, dialog.DefaultButton);
            Assert.Equal(ControlAppearance.Danger, dialog.PrimaryButtonAppearance);
            Assert.StartsWith(IrreversibleConfirmation.Label(kind), (string?)dialog.Title ?? "");
        });
    }
}
