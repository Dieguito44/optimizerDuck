using System.Collections.ObjectModel;
using System.IO;
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
    [NotifyCanExecuteChangedFor(nameof(ImportPresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportPresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseStandardPresetCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>Settings that failed or apps that could not be removed, by name.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>What "Aplicar" and "Revertir" use (plan F3-01). The standard one until a file is imported.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetText))]
    [NotifyPropertyChangedFor(nameof(IsCustomPreset))]
    private ServicePreset _preset = ServicePreset.Standard;

    public string PresetText =>
        Preset.IsBuiltIn
            ? Loc.Instance["Preset.Current.Standard"]
            : Loc.Instance["Preset.Current.Imported", Preset.Name, Preset.Settings.Count];

    public bool IsCustomPreset => !Preset.IsBuiltIn;

    /// <summary>
    ///     The Session page is measuring (found in the Sandbox tests, 19/11/2026): applying the
    ///     profile then would change the PC in the middle of the "before" measurement.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportPresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseStandardPresetCommand))]
    private bool _isSessionBusy;

    private bool CanRun() => !IsBusy && !IsSessionBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportPresetAsync()
    {
        ErrorText = null;
        Problems.Clear();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Instance["Preset.FileFilter"],
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = ServicePresetFile.Load(dialog.FileName, await runner.CatalogAsync());
            if (!result.IsValid)
            {
                ErrorText = Loc.Instance["Preset.Invalid", Path.GetFileName(dialog.FileName)];
                foreach (var problem in result.Problems)
                    Problems.Add(ProblemText(problem));
                return;
            }
            Preset = result.Preset!;
            StatusText = Loc.Instance["Preset.Imported", Preset.Name];
            logger.LogInformation(
                "Preset {Name} imported from {File}: {Settings} settings, {Apps} apps",
                Preset.Name,
                dialog.FileName,
                Preset.Settings.Count,
                Preset.Apps.Count + Preset.AppFragments.Count
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ExportPreset()
    {
        ErrorText = null;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.Instance["Preset.FileFilter"],
            FileName = SafeFileName(Preset.Name) + ".json",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, ServicePresetFile.Serialize(Preset));
            StatusText = Loc.Instance["Preset.Exported", dialog.FileName];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void UseStandardPreset()
    {
        Preset = ServicePreset.Standard;
        ErrorText = null;
        Problems.Clear();
        StatusText = Loc.Instance["Preset.Current.Standard"];
    }

    /// <summary>One line per problem, in the technician's language.</summary>
    internal static string ProblemText(PresetProblem problem) =>
        Loc.Instance[$"Preset.Problem.{problem.Kind}", problem.Detail ?? ""];

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "preset" : clean;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ApplyAsync() => ApplyPresetAsync(Preset);

    /// <summary>
    ///     Preview, confirmation, restore point and apply for any preset. The Optimize page's packs
    ///     (redesign, 19/11/2026) build one with the safe settings of a category and come here, so
    ///     they get the same checks as the "Servicio estándar".
    /// </summary>
    public async Task ApplyPresetAsync(ServicePreset preset)
    {
        if (!CanRun())
            return;
        ErrorText = null;
        Problems.Clear();
        try
        {
            // Preview first (plan F2-03): a dry run that changes nothing. Busy meanwhile, so the
            // button cannot start a second run.
            IsBusy = true;
            StatusText = Loc.Instance["Profile.Preview.Running"];
            var settings = await runner.SettingsAsync(preset);
            var apps = await runner.AppsToRemoveAsync(preset);
            var preview = await runner.PreviewAsync(preset, CancellationToken.None);
            IsBusy = false;
            StatusText = null;
            // Plan F1-09: removing apps is the part that cannot be undone; the dialog says so.
            var appNames =
                apps.Count == 0
                    ? Loc.Instance["Profile.Confirm.NoApps"]
                    : string.Join(", ", apps.Select(a => a.Name))
                        + Environment.NewLine
                        + Loc.Instance["Profile.Confirm.AppsIrreversible"];

            // Plan F1-09: the profile removes apps, which cannot be undone, so Cancel is the default
            // button (Enter never applies it). Found in the Sandbox tests (19/11/2026).
            var confirm = new ContentDialog
            {
                // A pack from Optimize has its own name and no apps: say so, not "Servicio estándar".
                Title = preset.IsBuiltIn
                    ? Loc.Instance["Profile.Confirm.Title"]
                    : Loc.Instance["Preset.Confirm.Title", preset.Name],
                Content = PreviewContent(
                    preset.Apps.Count == 0 && preset.AppFragments.Count == 0
                        ? Loc.Instance["Profile.Confirm.MessageNoApps", settings.Count]
                        : (preset.IsBuiltIn ? "" : Loc.Instance["Preset.Confirm.Name", preset.Name])
                            + Loc.Instance[
                                "Profile.Confirm.Message",
                                settings.Count,
                                apps.Count,
                                appNames
                            ],
                    preview
                ),
                PrimaryButtonText = Loc.Instance["Profile.Confirm.Apply"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
                DefaultButton = ContentDialogButton.Close,
            };
            var answer = await dialogService.ShowAsync(confirm, CancellationToken.None);
            if (answer != ContentDialogResult.Primary)
                return;

            if (!await EnsureRestorePointAsync())
                return;

            IsBusy = true;
            var progress = new Progress<Domain.UI.ProcessingProgress>(p => StatusText = p.Message);
            var result = await runner.ApplyAsync(
                preset,
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
            var result = await runner.RevertAsync(Preset, progress, CancellationToken.None);
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
        var changes = preview.Where(c => c.WillChange).ToList();
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
