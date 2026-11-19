using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Software;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>
///     "Measure temperature" card of the Diagnostic page (R-THERMAL). With consent, installs the
///     PawnIO driver with winget, measures the CPU for 20 seconds and uninstalls it again. If
///     PawnIO was already installed, it is used and left alone.
/// </summary>
public partial class TemperatureViewModel(
    SoftwareService softwareService,
    ToolRunPresenter presenter,
    IContentDialogService dialogs,
    ILogger<TemperatureViewModel> logger
) : ObservableObject
{
    private static readonly TimeSpan Sampling = TimeSpan.FromSeconds(20);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    private bool CanMeasure() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private async Task MeasureAsync()
    {
        var alreadyInstalled = ThermalService.IsPawnIoInstalled();
        if (!alreadyInstalled)
        {
            var answer = await dialogs.ShowSimpleDialogAsync(
                new SimpleContentDialogCreateOptions
                {
                    Title = Loc.Instance["Temperature.Confirm.Title"],
                    Content = Loc.Instance["Temperature.Confirm.Message"],
                    PrimaryButtonText = Loc.Instance["Temperature.Confirm.Measure"],
                    CloseButtonText = Loc.Instance["Button.Cancel"],
                }
            );
            if (answer != ContentDialogResult.Primary)
                return;
        }

        IsBusy = true;
        try
        {
            if (
                !alreadyInstalled
                && !await RunAsync(softwareService.BuildInstallRequest(SoftwareCatalog.PawnIo))
            )
            {
                StatusText = Loc.Instance["Temperature.InstallFailed"];
                return;
            }

            StatusText = Loc.Instance["Temperature.Measuring", (int)Sampling.TotalSeconds];
            var reading = await ThermalService.MeasureAsync(
                Sampling,
                logger,
                CancellationToken.None
            );
            StatusText = reading is { } r
                ? Loc.Instance[
                    "Temperature.Result",
                    r.Celsius.ToString("0.#", CultureInfo.CurrentCulture)
                ]
                : Loc.Instance["Temperature.NoSensor"];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Temperature measurement failed");
            StatusText = Loc.Instance["Temperature.NoSensor"];
        }
        finally
        {
            // Whatever happened, a driver Zentra installed does not stay on the client's PC.
            if (!alreadyInstalled && ThermalService.IsPawnIoInstalled())
                if (!await RunAsync(softwareService.BuildUninstallRequest(SoftwareCatalog.PawnIo)))
                    StatusText += " " + Loc.Instance["Temperature.UninstallFailed"];
            IsBusy = false;
        }
    }

    private async Task<bool> RunAsync(Domain.Execution.OperationRequest request)
    {
        var outcome = await presenter.RunAsync(request);
        return await presenter.ReportAsync(request, outcome, request.DisplayName);
    }
}
