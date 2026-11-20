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
    Services.System.SystemInfoService systemInfo,
    ILogger<TemperatureViewModel> logger
) : ObservableObject
{
    /// <summary>LatencyMon's official page (Resplendence Software, checked 2026-11-19).</summary>
    public const string LatencyMonUrl = "https://www.resplendence.com/latencymon";

    private CancellationTokenSource? _stressCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelStressCommand))]
    private bool _isStressing;

    private static readonly TimeSpan Sampling = TimeSpan.FromSeconds(20);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureCommand))]
    [NotifyCanExecuteChangedFor(nameof(StressCommand))]
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

    /// <summary>
    ///     Plan F4-02: the short load test, always after a confirmation that says what it does.
    ///     PawnIO is installed for it and removed afterwards, as for the measurement.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private async Task StressAsync()
    {
        var alreadyInstalled = ThermalService.IsPawnIoInstalled();
        var answer = await dialogs.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Stress.Confirm.Title"],
                Content =
                    Loc.Instance[
                        "Stress.Confirm.Message",
                        (int)ThermalStressTest.DefaultDuration.TotalSeconds,
                        (int)ThermalStressTest.DefaultLimitC
                    ] + (alreadyInstalled ? "" : " " + Loc.Instance["Temperature.Confirm.Message"]),
                PrimaryButtonText = Loc.Instance["Stress.Confirm.Start"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            }
        );
        if (answer != ContentDialogResult.Primary)
            return;

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

            var snapshot = await systemInfo.EnsureSnapshotAsync();
            var laptop = snapshot.Windows.DeviceKind == Services.System.DeviceKind.Laptop;
            _stressCts = new CancellationTokenSource();
            IsStressing = true;
            StatusText = Loc.Instance["Stress.Idle"];
            var progress = new Progress<(int Seconds, double Celsius)>(p =>
                StatusText = Loc.Instance[
                    "Stress.Running",
                    p.Seconds,
                    (int)ThermalStressTest.DefaultDuration.TotalSeconds,
                    p.Celsius.ToString("0", CultureInfo.CurrentCulture)
                ]
            );
            using var machine = new StressMachine(laptop);
            var result = await Task.Run(() =>
                new ThermalStressTest(machine, logger).RunAsync(
                    ThermalStressTest.DefaultDuration,
                    ThermalStressTest.DefaultLimitC,
                    progress,
                    _stressCts.Token
                )
            );
            StatusText = StressText(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stress test failed");
            StatusText = Loc.Instance["Temperature.NoSensor"];
        }
        finally
        {
            IsStressing = false;
            _stressCts?.Dispose();
            _stressCts = null;
            if (!alreadyInstalled && ThermalService.IsPawnIoInstalled())
                if (!await RunAsync(softwareService.BuildUninstallRequest(SoftwareCatalog.PawnIo)))
                    StatusText += " " + Loc.Instance["Temperature.UninstallFailed"];
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsStressing))]
    private void CancelStress() => _stressCts?.Cancel();

    /// <summary>Plan F4-03: LatencyMon, opened as the user, not elevated.</summary>
    [RelayCommand]
    private void OpenLatencyMon() => Common.Helpers.UnelevatedShell.Open(LatencyMonUrl);

    internal static string StressText(StressResult r)
    {
        var c = CultureInfo.CurrentCulture;
        var summary = Loc.Instance[
            "Stress.Summary",
            r.IdleC?.ToString("0", c) ?? "—",
            r.MaxC?.ToString("0", c) ?? "—",
            (int)r.Ran.TotalSeconds
        ];
        var throttle = r.ThrottleIncidents switch
        {
            null => Loc.Instance["Stress.Throttle.Unknown"],
            0 => Loc.Instance["Stress.Throttle.None"],
            var n => Loc.Instance["Stress.Throttle.Some", n],
        };
        return r.Outcome switch
        {
            StressOutcome.NoSensor => Loc.Instance["Temperature.NoSensor"],
            StressOutcome.OnBattery when r.Ran == TimeSpan.Zero => Loc.Instance[
                "Stress.NeedsCharger"
            ],
            _ => $"{Loc.Instance[$"Stress.Outcome.{r.Outcome}"]} {summary} {throttle}",
        };
    }

    private async Task<bool> RunAsync(Domain.Execution.OperationRequest request)
    {
        var outcome = await presenter.RunAsync(request);
        return await presenter.ReportAsync(request, outcome, request.DisplayName);
    }
}
