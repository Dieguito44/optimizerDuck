using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Ai;
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
    AiReportService aiReportService,
    ApiKeyStore apiKeyStore,
    ServiceProfileViewModel profile,
    GameModeViewModel gameMode,
    TimeProvider timeProvider,
    ILogger<SessionViewModel> logger
) : ViewModel
{
    private CancellationTokenSource? _cts;
    private ServiceSession? _session;

    /// <summary>The "Servicio estándar" card (phase 6).</summary>
    public ServiceProfileViewModel Profile { get; } = profile;

    /// <summary>The "Modo juego avanzado" card (phase 7), visible only while it is applied.</summary>
    public GameModeViewModel GameMode { get; } = gameMode;

    public override void OnNavigatedTo() => _ = GameMode.RefreshAsync();

    [ObservableProperty]
    private string _clientLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(MeasureBeforeCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasureAfterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GeneratePdfCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteReportCommand))]
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
        ShowWritten(null);
        StatusText = string.Empty;
        RefreshCommands();
    }

    // ----- AI report (phase 5) -----------------------------------------------------------

    [ObservableProperty]
    private bool _rememberKey;

    [ObservableProperty]
    private string? _keyErrorText;

    [ObservableProperty]
    private string? _writtenSummary;

    [ObservableProperty]
    private string? _writtenSource;

    public bool HasKey => apiKeyStore.HasKey;
    public bool CanRememberKey => apiKeyStore.CanRemember;

    /// <summary>Zentra runs as another administrator than the person at the PC (plan F2-05).</summary>
    public bool IsUserRedirected => optimizerDuck.Services.System.InteractiveUser.IsRedirected;
    public bool HasWritten => WrittenSummary is not null;

    public string KeyStatusText =>
        !apiKeyStore.HasKey ? Loc.Instance["Session.Ai.Key.None"]
        : apiKeyStore.IsRemembered ? Loc.Instance["Session.Ai.Key.Remembered"]
        : Loc.Instance["Session.Ai.Key.InMemory"];

    public ObservableCollection<WrittenPriority> WrittenPriorities { get; } = [];
    public ObservableCollection<string> WrittenWarnings { get; } = [];

    /// <summary>
    ///     Reads the key from the standard WPF PasswordBox (no binding: a bound password would sit
    ///     in a string property) and clears the box right away.
    /// </summary>
    [RelayCommand]
    private void UseKey(object? parameter)
    {
        if (parameter is not System.Windows.Controls.PasswordBox box)
            return;
        var error = apiKeyStore.Set(box.Password, RememberKey);
        box.Clear();
        KeyErrorText = error is { } e ? Loc.Instance[$"Session.Ai.KeyError.{e}"] : null;
        RefreshKey();
    }

    [RelayCommand]
    private void ForgetKey()
    {
        apiKeyStore.Forget();
        RememberKey = false;
        KeyErrorText = null;
        RefreshKey();
    }

    [RelayCommand(CanExecute = nameof(CanGeneratePdf))]
    private async Task WriteReportAsync()
    {
        if (_session is null)
            return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            await WriteReportCoreAsync();
            sessionStore.Save(_session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Report writing failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task WriteReportCoreAsync()
    {
        StatusText = Loc.Instance[
            apiKeyStore.HasKey ? "Session.Ai.Writing" : "Session.Ai.WritingTemplate"
        ];
        var model = await pdfReportService.BuildModelAsync(_session!);
        var record = await Task.Run(() =>
            aiReportService.WriteAsync(_session!, model, CancellationToken.None)
        );
        _session = _session! with { Report = record };
        ShowWritten(record);
        RefreshKey();
        StatusText =
            record.Source == ReportSource.Ai
                ? Loc.Instance["Session.Ai.Done", record.InputTokens ?? 0, record.OutputTokens ?? 0]
                : Loc.Instance[
                    $"Session.Ai.Template.{record.TemplateReason ?? TemplateReason.NoKey}"
                ];
    }

    private void ShowWritten(AiReportRecord? record)
    {
        var written = WrittenReport.From(record);
        WrittenSummary = written?.Summary;
        WrittenSource = written?.Source;
        WrittenPriorities.Clear();
        WrittenWarnings.Clear();
        foreach (var p in written?.Priorities ?? [])
            WrittenPriorities.Add(p);
        foreach (var w in written?.Warnings ?? [])
            WrittenWarnings.Add(w);
        OnPropertyChanged(nameof(HasWritten));
    }

    private void RefreshKey()
    {
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(KeyStatusText));
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
            // Every PDF carries a written report: the AI's when there is a key, the template's if not.
            if (_session.Report is null)
                await WriteReportCoreAsync();
            StatusText = Loc.Instance["Session.Step.Pdf"];
            sessionStore.Save(_session);
            LastPdfPath = await pdfReportService.GenerateAsync(_session);
            StatusText = Loc.Instance["Session.Pdf.Saved", LastPdfPath];
            UnelevatedShell.Open(LastPdfPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PDF generation failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
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
            UnelevatedShell.Open(folder);
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

            // Measure first: the diagnosis uses the RAM at rest (R-RAM-IDLE-HIGH), and running the
            // diagnosis first would load the PC during the rest measurement.
            var measurement = await Task.Run(() =>
                measurementService.MeasureAsync(phase, progress, _cts.Token)
            );
            StatusText = Loc.Instance["Session.Step.Diagnosis"];
            var report = await Task.Run(() =>
                diagnosticService.RunAsync(measurement.IdleRamPercent, _cts.Token)
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
            // A new measurement makes the written report stale.
            _session = _session with
            {
                Report = null,
            };
            ShowWritten(null);
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
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
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
        WriteReportCommand.NotifyCanExecuteChanged();
    }
}
