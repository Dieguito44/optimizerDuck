using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Software;
using optimizerDuck.Services.System;
using Wpf.Ui.Extensions;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>One catalog program on the page, with whether winget sees it installed.</summary>
public partial class SoftwareItem(SoftwarePackage package) : ObservableObject
{
    public SoftwarePackage Package { get; } = package;
    public string Name => Package.Name;
    public string Description => Package.Description;
    public string Details => $"{Package.WingetId} · {Package.License}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool? _isInstalled;

    public string StatusText =>
        IsInstalled switch
        {
            true => Loc.Instance["Software.Status.Installed"],
            false => Loc.Instance["Software.Status.NotInstalled"],
            null => Loc.Instance["Software.Status.Unknown"],
        };

    public bool CanInstall => IsInstalled != true;
    public bool CanUninstall => IsInstalled == true;
}

/// <summary>
///     "Programs" page: the closed winget catalog. Every install and uninstall goes through the
///     shared tool presenter (progress dialog, retries, history).
/// </summary>
public partial class SoftwareViewModel(
    SoftwareService softwareService,
    ToolRunPresenter presenter,
    SystemInfoService systemInfoService,
    Wpf.Ui.IContentDialogService dialogs,
    ILogger<SoftwareViewModel> logger
) : ViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    public ObservableCollection<SoftwareItem> Items { get; } = [];

    /// <summary>Official driver pages for the GPUs winget does not cover.</summary>
    public IReadOnlyList<string> DriverPages { get; } =
    [
        "https://www.amd.com/es/support/download/drivers.html",
        "https://www.nvidia.com/es-la/drivers/",
    ];

    protected override async Task InitializeOnceAsync()
    {
        var system = await systemInfoService.EnsureSnapshotAsync();
        foreach (var package in SoftwareCatalog.For(system))
            Items.Add(new SoftwareItem(package));
        await RefreshAsync();
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = Loc.Instance["Software.Checking"];
        try
        {
            foreach (var item in Items)
                item.IsInstalled = await softwareService.IsInstalledAsync(
                    item.Package,
                    CancellationToken.None
                );
            StatusText = Items.Any(i => i.IsInstalled is null)
                ? Loc.Instance["Software.WingetMissing"]
                : null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task InstallAsync(SoftwareItem? item) =>
        RunAsync(item, softwareService.BuildInstallRequest);

    /// <summary>Plan F1-09: uninstalling cannot be undone from Zentra, so it asks first.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallAsync(SoftwareItem? item)
    {
        if (item is null)
            return;
        if (
            !await optimizerDuck.UI.Dialogs.IrreversibleConfirmation.AskAsync(
                dialogs,
                Domain.UI.OptimizationConfirmation.Irreversible,
                Loc.Instance["Irreversible.Uninstall", item.Name],
                Loc.Instance["Irreversible.Why.Uninstall"]
            )
        )
            return;
        await RunAsync(item, softwareService.BuildUninstallRequest);
    }

    /// <summary>
    ///     ACT-WINGET-UPGRADE. Behind a confirmation: updates cannot be undone from Zentra, and a
    ///     program the client keeps on an old version on purpose would be updated too.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpgradeAllAsync()
    {
        var answer = await dialogs.ShowSimpleDialogAsync(
            new Wpf.Ui.SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Software.UpgradeAll.Confirm.Title"],
                Content = Loc.Instance["Software.UpgradeAll.Confirm.Message"],
                PrimaryButtonText = Loc.Instance["Software.Button.UpgradeAll"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            }
        );
        if (answer != Wpf.Ui.Controls.ContentDialogResult.Primary)
            return;

        IsBusy = true;
        try
        {
            var request = softwareService.BuildUpgradeAllRequest();
            var outcome = await presenter.RunAsync(request);
            await presenter.ReportAsync(
                request,
                outcome,
                Loc.Instance["Software.Button.UpgradeAll"]
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "winget upgrade --all failed");
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private static void OpenDriverPage(string? url)
    {
        // Only the two fixed pages above; never a URL from anywhere else.
        if (
            url is null
            || !url.StartsWith("https://www.amd.com/") && !url.StartsWith("https://www.nvidia.com/")
        )
            return;
        UnelevatedShell.Open(url);
    }

    private async Task RunAsync(
        SoftwareItem? item,
        Func<SoftwarePackage, Domain.Execution.OperationRequest> build
    )
    {
        if (item is null)
            return;
        IsBusy = true;
        try
        {
            var request = build(item.Package);
            var outcome = await presenter.RunAsync(request);
            await presenter.ReportAsync(request, outcome, item.Name);
            item.IsInstalled = await softwareService.IsInstalledAsync(
                item.Package,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "winget run failed for {Id}", item.Package.WingetId);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
