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
    ToolRunPresenter presenter,
    ILogger<RepairViewModel> logger
) : ViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRepairCommand))]
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
}
