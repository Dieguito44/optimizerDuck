using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>
///     The "Servicio estándar" card of the Session page: apply the profile between the "before"
///     and the "after" measurements, or revert it. Always behind a confirmation and a restore point.
/// </summary>
public partial class ServiceProfileViewModel(
    ServiceProfileRunner runner,
    OptimizationService optimizationService,
    IContentDialogService dialogService,
    ILogger<ServiceProfileViewModel> logger
) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>Settings that failed or apps that could not be removed, by name.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyAsync()
    {
        ErrorText = null;
        Problems.Clear();
        try
        {
            // Preview first (plan F2-03): a dry run that changes nothing. Busy meanwhile, so the
            // button cannot start a second run.
            IsBusy = true;
            StatusText = Loc.Instance["Profile.Preview.Running"];
            var settings = await runner.SettingsAsync();
            var apps = await runner.AppsToRemoveAsync();
            var preview = await runner.PreviewAsync(CancellationToken.None);
            IsBusy = false;
            StatusText = null;
            var appNames =
                apps.Count == 0
                    ? Loc.Instance["Profile.Confirm.NoApps"]
                    : string.Join(", ", apps.Select(a => a.Name));

            var answer = await dialogService.ShowSimpleDialogAsync(
                new SimpleContentDialogCreateOptions
                {
                    Title = Loc.Instance["Profile.Confirm.Title"],
                    Content = PreviewContent(
                        Loc.Instance[
                            "Profile.Confirm.Message",
                            settings.Count,
                            apps.Count,
                            appNames
                        ],
                        preview
                    ),
                    PrimaryButtonText = Loc.Instance["Profile.Confirm.Apply"],
                    CloseButtonText = Loc.Instance["Button.Cancel"],
                }
            );
            if (answer != ContentDialogResult.Primary)
                return;

            if (!await EnsureRestorePointAsync())
                return;

            IsBusy = true;
            var progress = new Progress<Domain.UI.ProcessingProgress>(p => StatusText = p.Message);
            var result = await runner.ApplyAsync(
                removeApps: true,
                progress,
                CancellationToken.None
            );
            Show(result, applied: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Standard profile failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
            if (Application.Current is App app)
                app.HasPendingChanges = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RevertAsync()
    {
        ErrorText = null;
        Problems.Clear();
        try
        {
            var answer = await dialogService.ShowSimpleDialogAsync(
                new SimpleContentDialogCreateOptions
                {
                    Title = Loc.Instance["Profile.Revert.Title"],
                    Content = Loc.Instance["Profile.Revert.Message"],
                    PrimaryButtonText = Loc.Instance["Profile.Revert.Confirm"],
                    CloseButtonText = Loc.Instance["Button.Cancel"],
                }
            );
            if (answer != ContentDialogResult.Primary)
                return;

            IsBusy = true;
            var progress = new Progress<Domain.UI.ProcessingProgress>(p => StatusText = p.Message);
            var result = await runner.RevertAsync(progress, CancellationToken.None);
            Show(result, applied: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Standard profile revert failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
            if (Application.Current is App app)
                app.HasPendingChanges = true;
        }
    }

    /// <summary>
    ///     The confirmation text plus the preview: every value the profile would change on this
    ///     PC, grouped by setting, with what is there now and what it becomes (plan F2-03).
    /// </summary>
    private static object PreviewContent(
        string message,
        IReadOnlyList<Domain.Execution.PlannedChange> preview
    )
    {
        var changes = preview.Where(c => !c.IsAlreadyThere).ToList();
        var panel = new global::System.Windows.Controls.StackPanel { MaxWidth = 640 };
        panel.Children.Add(
            new global::System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        panel.Children.Add(
            new global::System.Windows.Controls.TextBlock
            {
                Margin = new Thickness(0, 12, 0, 4),
                FontWeight = FontWeights.SemiBold,
                Text =
                    changes.Count == 0
                        ? Loc.Instance["Profile.Preview.None"]
                        : Loc.Instance[
                            "Profile.Preview.Header",
                            changes.Count,
                            changes.Select(c => c.Setting).Distinct().Count()
                        ],
                TextWrapping = TextWrapping.Wrap,
            }
        );
        if (changes.Count == 0)
            return panel;

        var lines = new global::System.Text.StringBuilder();
        foreach (var group in changes.GroupBy(c => c.Setting))
        {
            lines.AppendLine(group.Key);
            foreach (var change in group)
                lines.AppendLine(PreviewLine(change));
        }
        panel.Children.Add(
            new global::System.Windows.Controls.ScrollViewer
            {
                MaxHeight = 280,
                VerticalScrollBarVisibility = global::System
                    .Windows
                    .Controls
                    .ScrollBarVisibility
                    .Auto,
                Content = new global::System.Windows.Controls.TextBlock
                {
                    Text = lines.ToString().TrimEnd(),
                    FontFamily = new global::System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            }
        );
        return panel;
    }

    /// <summary>"   Registro · HKCU\...\Value: 1 → 0" (missing values say so).</summary>
    internal static string PreviewLine(Domain.Execution.PlannedChange change) =>
        $"   {Loc.Instance[$"Profile.Preview.Kind.{change.Kind}"]} · {change.Target}: "
        + $"{change.Current ?? Loc.Instance["Profile.Preview.Missing"]} → {change.Next}";

    /// <summary>
    ///     No restore point, no changes. Windows makes one point per 24 hours: when it skips, the
    ///     point from earlier today is the one that covers this change.
    /// </summary>
    private async Task<bool> EnsureRestorePointAsync()
    {
        var result = await optimizationService.CreateRestorePointAsync();
        switch (result)
        {
            case RestorePointResult.Success:
                optimizationService.WasRequestedRestorePoint = true;
                return true;
            case RestorePointResult.FrequencyLimitReached:
                optimizationService.WasRequestedRestorePoint = true;
                StatusText = Loc.Instance["Profile.RestorePoint.Recent"];
                return true;
            default:
                ErrorText = Loc.Instance["Profile.RestorePoint.Failed"];
                return false;
        }
    }

    private void Show(ProfileRunResult result, bool applied)
    {
        StatusText = applied
            ? Loc.Instance[
                "Profile.Done.Applied",
                result.Count(ProfileStepOutcome.Applied),
                result.Count(ProfileStepOutcome.AlreadyOptimal)
                    + result.Count(ProfileStepOutcome.AlreadyApplied),
                result.Count(ProfileStepOutcome.NotApplicable),
                result.Count(ProfileStepOutcome.Failed),
                result.AppsRemoved.Count
            ]
            : Loc.Instance[
                "Profile.Done.Reverted",
                result.Count(ProfileStepOutcome.Reverted),
                result.Count(ProfileStepOutcome.NothingToRevert),
                result.Count(ProfileStepOutcome.Failed)
            ];

        foreach (var s in result.Settings.Where(s => s.Outcome == ProfileStepOutcome.Failed))
            Problems.Add(Loc.Instance["Profile.Problem.Setting", s.Name]);
        foreach (var name in result.AppsFailed)
            Problems.Add(Loc.Instance["Profile.Problem.App", name]);
    }
}
