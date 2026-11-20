using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>
///     Two cards of the Diagnostic page: internet speed (plan F4-06, after a consent that says
///     Cloudflare sees the client's IP) and the manufacturer's warranty (plan F4-07).
/// </summary>
public partial class ServiceToolsViewModel(
    SpeedTestService speedTest,
    IContentDialogService dialogs,
    ILogger<ServiceToolsViewModel> logger
) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SpeedTestCommand))]
    private bool _isTesting;

    [ObservableProperty]
    private string? _speedText;

    [ObservableProperty]
    private string? _warrantyText;

    private bool CanTest() => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task SpeedTestAsync()
    {
        var answer = await dialogs.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Speed.Confirm.Title"],
                Content = Loc.Instance["Speed.Confirm.Message", SpeedTestService.Host],
                PrimaryButtonText = Loc.Instance["Speed.Confirm.Start"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            }
        );
        if (answer != ContentDialogResult.Primary)
            return;

        IsTesting = true;
        try
        {
            var progress = new Progress<string>(step =>
                SpeedText = Loc.Instance[$"Speed.Step.{step}"]
            );
            var r = await Task.Run(() => speedTest.RunAsync(progress, CancellationToken.None));
            var c = CultureInfo.CurrentCulture;
            SpeedText =
                r.DownloadMbps is null && r.UploadMbps is null
                    ? Loc.Instance["Speed.Failed"]
                    : Loc.Instance[
                        "Speed.Result",
                        r.DownloadMbps?.ToString("0.#", c) ?? Loc.Instance["Report.NotMeasured"],
                        r.UploadMbps?.ToString("0.#", c) ?? Loc.Instance["Report.NotMeasured"],
                        r.LatencyMs?.ToString("0", c) ?? Loc.Instance["Report.NotMeasured"]
                    ];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speed test failed");
            SpeedText = Loc.Instance["Speed.Failed"];
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Copies the serial and opens the manufacturer's page. The serial is not put in the address.</summary>
    [RelayCommand]
    private async Task WarrantyAsync()
    {
        var info = await Task.Run(WarrantyLookup.Read);
        var brand = info.Manufacturer ?? Loc.Instance["Warranty.UnknownBrand"];
        if (info.Serial is null)
        {
            WarrantyText = Loc.Instance["Warranty.NoSerial", brand];
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(info.Serial);
        }
        catch (System.Runtime.InteropServices.COMException) { }

        if (info.LookupUrl is { } url)
        {
            UnelevatedShell.Open(url);
            WarrantyText = Loc.Instance["Warranty.Opened", brand, info.Model ?? "", info.Serial];
        }
        else
            WarrantyText = Loc.Instance["Warranty.NoPage", brand, info.Model ?? "", info.Serial];
    }
}
