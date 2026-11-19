using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Reports;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>
///     Service session page: measure before, apply optimizations in the other pages, measure
///     after, and write the client PDF. Measuring is read-only.
/// </summary>
public partial class SessionViewModel(
    DiagnosticService diagnosticService,
    MeasurementService measurementService,
    SessionStore sessionStore,
    PdfReportService pdfReportService,
    TimeProvider timeProvider,
    ILogger<SessionViewModel> logger
) : ViewModel
{
    private CancellationTokenSource? _cts;
    private ServiceSession? _session;

    [ObservableProperty]
    private string _clientLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(MeasureBeforeCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasureAfterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GeneratePdfCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewSessionCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string _scoreBeforeText = "—";

    [ObservableProperty]
    private string _scoreAfterText = "—";

    [ObservableProperty]
    private string? _lastPdfPath;

    public bool IsIdle => !IsBusy;

    public bool HasMetrics => Metrics.Count > 0;

    public ObservableCollection<MetricRow> Metrics { get; } = [];

    private bool CanMeasureBefore() => !IsBusy;

    private bool CanMeasureAfter() => !IsBusy && _session?.Before is not null;

    private bool CanGeneratePdf() => !IsBusy && _session?.Before is not null;

    [RelayCommand(CanExecute = nameof(CanMeasureBefore))]
    private Task MeasureBeforeAsync() => MeasureAsync(MeasurementPhase.Before);

    [RelayCommand(CanExecute = nameof(CanMeasureAfter))]
    private Task MeasureAfterAsync() => MeasureAsync(MeasurementPhase.After);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void NewSession()
    {
        _session = null;
        LastPdfPath = null;
        ScoreBeforeText = ScoreAfterText = "—";
        Metrics.Clear();
        OnPropertyChanged(nameof(HasMetrics));
        StatusText = string.Empty;
        RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanGeneratePdf))]
    private async Task GeneratePdfAsync()
    {
        if (_session is null)
            return;
        IsBusy = true;
        ErrorText = null;
        StatusText = Loc.Instance["Session.Step.Pdf"];
        try
        {
            _session = _session with { ClientLabel = ClientLabel };
            sessionStore.Save(_session);
            LastPdfPath = await pdfReportService.GenerateAsync(_session);
            StatusText = Loc.Instance["Session.Pdf.Saved", LastPdfPath];
            Process.Start(new ProcessStartInfo(LastPdfPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PDF generation failed");
            ErrorText = Loc.Instance["Session.Error", ex.Message];
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_session is null)
            return;
        var folder = SessionStore.FolderFor(_session);
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private async Task MeasureAsync(MeasurementPhase phase)
    {
        IsBusy = true;
        ErrorText = null;
        _cts = new CancellationTokenSource();
        var progress = new Progress<MeasurementProgress>(p =>
            StatusText = Loc.Instance[$"Session.Step.{p.Step}", p.SecondsLeft]
        );

        try
        {
            _session ??= new ServiceSession
            {
                CreatedAtUtc = timeProvider.GetUtcNow(),
                ClientLabel = ClientLabel,
            };

            StatusText = Loc.Instance["Session.Step.Diagnosis"];
            var report = await Task.Run(() => diagnosticService.RunAsync(_cts.Token));
            var measurement = await Task.Run(() =>
                measurementService.MeasureAsync(phase, progress, _cts.Token)
            );

            var summary = DiagnosisSummary.From(report);
            _session =
                phase == MeasurementPhase.Before
                    ? _session with
                    {
                        Before = measurement,
                        DiagnosisBefore = summary,
                        ClientLabel = ClientLabel,
                    }
                    : _session with
                    {
                        After = measurement,
                        DiagnosisAfter = summary,
                        ClientLabel = ClientLabel,
                    };
            sessionStore.Save(_session);
            ShowSession();
            StatusText = Loc.Instance["Session.Step.Done", 0];
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Instance["Session.Cancelled"];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Measurement {Phase} failed", phase);
            ErrorText = Loc.Instance["Session.Error", ex.Message];
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            RefreshCommands();
        }
    }

    private void ShowSession()
    {
        if (_session is null)
            return;
        ScoreBeforeText = _session.DiagnosisBefore?.Score.Score.ToString() ?? "—";
        ScoreAfterText = _session.DiagnosisAfter?.Score.Score.ToString() ?? "—";
        Metrics.Clear();
        foreach (
            var row in SessionReportModel.BuildMetrics(
                _session.Before,
                _session.After,
                Loc.CurrentCulture
            )
        )
            Metrics.Add(row);
        OnPropertyChanged(nameof(HasMetrics));
    }

    private void RefreshCommands()
    {
        MeasureAfterCommand.NotifyCanExecuteChanged();
        GeneratePdfCommand.NotifyCanExecuteChanged();
    }
}
