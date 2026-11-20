using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Repair;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>One finished repair step on the page.</summary>
public sealed record RepairStepItem(string Name, string Outcome, string Summary);

/// <summary>
///     "Repair Windows" page. The run goes through the shared tool presenter, so it shows the
///     progress dialog, offers retries and writes the history like every other tool.
/// </summary>
public partial class RepairViewModel(
    RepairService repairService,
    DefenderScanService defenderScanService,
    SafetyScannerService safetyScannerService,
    Wpf.Ui.IContentDialogService dialogs,
    ToolRunPresenter presenter,
    ILogger<RepairViewModel> logger
) : ViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(FullScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(OfflineScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(SafetyScanCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasResults;

    public ObservableCollection<RepairStepItem> Steps { get; } = [];

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunRepairAsync()
    {
        IsRunning = true;
        Steps.Clear();
        HasResults = false;
        var results = new List<RepairStepResult>();
        try
        {
            var request = repairService.BuildRequest(results);
            var outcome = await presenter.RunAsync(request);
            await presenter.ReportAsync(request, outcome, Loc.Instance["Repair.Header.Title"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Windows repair failed");
        }
        finally
        {
            foreach (var r in results)
                Steps.Add(
                    new RepairStepItem(
                        Loc.Instance[$"Repair.Step.{r.Step}"],
                        Loc.Instance[$"Repair.Outcome.{r.Outcome}"],
                        r.Summary
                    )
                );
            HasResults = Steps.Count > 0;
            IsRunning = false;
        }
    }

    /// <summary>ACT-DEFENDER-FULL: a full Defender scan. Can take an hour or more.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task FullScanAsync() => RunToolAsync(defenderScanService.BuildFullScanRequest());

    /// <summary>ACT-DEFENDER-OFFLINE: restarts the PC into the offline scanner, after a warning.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task OfflineScanAsync()
    {
        var answer = await Wpf.Ui.Extensions.ContentDialogServiceExtensions.ShowSimpleDialogAsync(
            dialogs,
            new Wpf.Ui.SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Defender.Offline.Title"],
                Content = Loc.Instance["Defender.Offline.Warning"],
                PrimaryButtonText = Loc.Instance["Defender.Offline.Confirm"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            }
        );
        if (answer == Wpf.Ui.Controls.ContentDialogResult.Primary)
            await RunToolAsync(defenderScanService.BuildOfflineScanRequest());
    }

    /// <summary>
    ///     Plan F3-08: Microsoft Safety Scanner, after saying what it downloads and that it only
    ///     detects. The result goes to the results list, with the copy of its log.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SafetyScanAsync()
    {
        var answer = await Wpf.Ui.Extensions.ContentDialogServiceExtensions.ShowSimpleDialogAsync(
            dialogs,
            new Wpf.Ui.SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["SafetyScanner.Title"],
                Content = Loc.Instance["SafetyScanner.Confirm"],
                PrimaryButtonText = Loc.Instance["SafetyScanner.Start"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            }
        );
        if (answer != Wpf.Ui.Controls.ContentDialogResult.Primary)
            return;

        SafetyScanResult? result = null;
        await RunToolAsync(safetyScannerService.BuildRequest(r => result = r));
        if (result is null)
            return;

        Steps.Add(
            new RepairStepItem(
                Loc.Instance["SafetyScanner.Title"],
                Loc.Instance[$"SafetyScanner.Outcome.{result.Outcome}", result.Threats.Count],
                result.Threats.Count > 0
                    ? string.Join(", ", result.Threats)
                        + (result.LogCopyPath is { } log ? $" · {log}" : "")
                    : result.LogCopyPath ?? ""
            )
        );
        HasResults = true;
    }

    private async Task RunToolAsync(Domain.Execution.OperationRequest request)
    {
        IsRunning = true;
        try
        {
            var outcome = await presenter.RunAsync(request);
            await presenter.ReportAsync(request, outcome, request.DisplayName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Tool} failed", request.DisplayName);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
