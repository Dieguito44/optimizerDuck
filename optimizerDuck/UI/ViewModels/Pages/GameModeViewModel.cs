using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Optimization;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>
///     The "Modo juego avanzado" card of the Session page (phase 7). It only shows while the
///     option is applied, so nobody forgets that Memory Integrity is off, and its button turns
///     the protection back on through the normal revert.
/// </summary>
public partial class GameModeViewModel(
    OptimizationRegistry registry,
    OptimizationService optimizationService,
    IContentDialogService dialogService,
    ILogger<GameModeViewModel> logger
) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TurnProtectionOnCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isApplied;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    private async Task<IOptimization?> FindAsync()
    {
        await registry.EnsurePreloadedAsync();
        return registry
            .OptimizationCategories.SelectMany(c => c.Optimizations)
            .FirstOrDefault(o => o is Performance.AdvancedGameMode);
    }

    /// <summary>Re-reads whether the option is applied (its revert data is on disk).</summary>
    public async Task RefreshAsync()
    {
        try
        {
            if (await FindAsync() is not { } optimization)
                return;
            await OptimizationService.UpdateOptimizationStateAsync(optimization);
            IsApplied = optimization.State.IsApplied;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the advanced game mode state");
        }
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TurnProtectionOnAsync()
    {
        ErrorText = null;
        try
        {
            var answer = await dialogService.ShowSimpleDialogAsync(
                new SimpleContentDialogCreateOptions
                {
                    Title = Loc.Instance["GameMode.Revert.Title"],
                    Content = Loc.Instance["GameMode.Revert.Message"],
                    PrimaryButtonText = Loc.Instance["GameMode.Revert.Confirm"],
                    CloseButtonText = Loc.Instance["Button.Cancel"],
                }
            );
            if (answer != ContentDialogResult.Primary)
                return;
            if (await FindAsync() is not { } optimization)
                return;

            IsBusy = true;
            var progress = new Progress<Domain.UI.ProcessingProgress>(p => StatusText = p.Message);
            var result = await optimizationService.RevertAsync(optimization, progress);
            StatusText = result.Success
                ? Loc.Instance["GameMode.Revert.Done"]
                : Loc.Instance["GameMode.Revert.Failed", result.Message];
            if (Application.Current is App app)
                app.HasPendingChanges = true; // the protection is back after a restart
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Advanced game mode revert failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }
}
