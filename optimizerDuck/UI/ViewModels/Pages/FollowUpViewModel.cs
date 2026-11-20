using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.FollowUp;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>
///     The "Seguimiento" card of the Session page (plan of improvements F3-05): schedule the
///     diagnosis-only checks at 7 and 30 days, with the client's written consent, or remove them.
/// </summary>
public partial class FollowUpViewModel(
    FollowUpService followUp,
    TimeProvider timeProvider,
    ILogger<FollowUpViewModel> logger
) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScheduleCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduled))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>The client label typed in the session, for the follow-up reports.</summary>
    public string? ClientLabel { get; set; }

    public bool IsScheduled => FollowUpService.ReadState() is { IsFinished: false };

    public bool HasResults =>
        Directory.Exists(FollowUpService.Folder)
        && Directory.EnumerateDirectories(FollowUpService.Folder, "*-dias_*").Any();

    /// <summary>Reads the state again (the page calls it when it opens).</summary>
    public void Refresh()
    {
        var state = FollowUpService.ReadState();
        StatusText = state switch
        {
            null => Loc.Instance["FollowUp.Status.None"],
            { IsFinished: true } => Loc.Instance["FollowUp.Status.Done", state.Runs.Count],
            _ => Loc.Instance[
                "FollowUp.Status.Scheduled",
                string.Join(
                    " · ",
                    state.DueAtUtc.Select(d => d.ToLocalTime().ToString("d", Loc.CurrentCulture))
                ),
                state.Runs.Count
            ],
        };
        OnPropertyChanged(nameof(HasResults));
    }

    private bool CanSchedule() => !IsBusy;

    private bool CanRemove() => !IsBusy && IsScheduled;

    [RelayCommand(CanExecute = nameof(CanSchedule))]
    private async Task ScheduleAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            var result = await followUp.ScheduleAsync(
                ClientLabel,
                timeProvider.GetLocalNow(),
                CancellationToken.None
            );
            if (result != FollowUpScheduleResult.Scheduled)
                ErrorText = Loc.Instance[$"FollowUp.Result.{result}"];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Follow-up scheduling failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            if (!await followUp.RemoveAsync(CancellationToken.None))
                ErrorText = Loc.Instance["FollowUp.RemoveFailed"];
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void OpenResults()
    {
        if (Directory.Exists(FollowUpService.Folder))
            UnelevatedShell.Open(FollowUpService.Folder);
    }
}
