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
using Wpf.Ui.Extensions;
using optimizerDuck.Services.Reports;

using optimizerDuck.Services.System;

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
    SystemInfoService systemInfo,
    GameModeViewModel gameMode,
    FollowUpViewModel followUp,
    TimeProvider timeProvider,
    Wpf.Ui.IContentDialogService dialogService,
    ILogger<SessionViewModel> logger
) : ViewModel
{
    private CancellationTokenSource? _cts;
    private ServiceSession? _session;

    /// <summary>The "Servicio estándar" card (phase 6).</summary>
    public ServiceProfileViewModel Profile { get; } = profile;

    /// <summary>The "Modo juego avanzado" card (phase 7), visible only while it is applied.</summary>
    public GameModeViewModel GameMode { get; } = gameMode;

    /// <summary>The "Seguimiento" card (plan of improvements F3-05).</summary>
    public FollowUpViewModel FollowUp { get; } = followUp;

    private bool _profileWired;

    public override void OnNavigatedTo()
    {
        _ = SuggestProfileAsync();
        ResumeUnfinishedSession();
        if (!_profileWired)
        {
            _profileWired = true;
            Profile.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ServiceProfileViewModel.IsBusy))
                {
                    MeasureBeforeCommand.NotifyCanExecuteChanged();
                    MeasureAfterCommand.NotifyCanExecuteChanged();
                }
            };
        }
        _ = GameMode.RefreshAsync();
        FollowUp.Refresh();
    }

    [ObservableProperty]
    private string _clientLabel = string.Empty;

    partial void OnClientLabelChanged(string value) => FollowUp.ClientLabel = value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(MeasureBeforeCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasureAfterCommand))]
    [NotifyCanExecuteChangedFor(nameof(GeneratePdfCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportInventoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportAiRequestCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportAiReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteFromRequestCommand))]
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

    partial void OnLastPdfPathChanged(string? value) => OnPropertyChanged(nameof(HasPdf));

    // The "done" chips of the steps (redesign, 19/11/2026).
    public bool HasBefore => _session?.Before is not null;
    public bool HasAfter => _session?.After is not null;
    public bool HasPdf => LastPdfPath is not null;

    private void RefreshSteps()
    {
        OnPropertyChanged(nameof(HasBefore));
        OnPropertyChanged(nameof(HasAfter));
        OnPropertyChanged(nameof(HasPdf));
    }

    public bool IsIdle => !IsBusy;

    public bool HasMetrics => Metrics.Count > 0;

    public ObservableCollection<MetricRow> Metrics { get; } = [];

    // The profile card and the measurements never run at the same time.
    partial void OnIsBusyChanged(bool value) => Profile.IsSessionBusy = value;

    private bool CanMeasureBefore() => !IsBusy && !Profile.IsBusy;

    private bool CanMeasureAfter() => !IsBusy && !Profile.IsBusy && _session?.Before is not null;

    private bool CanGeneratePdf() => !IsBusy && _session?.Before is not null;

    [RelayCommand(CanExecute = nameof(CanMeasureBefore))]
    private Task MeasureBeforeAsync() => MeasureAsync(MeasurementPhase.Before);

    private bool _resumeChecked;
    private bool _resuming;

    /// <summary>
    ///     After the restart the page asks for, picks up the session that was measured "before"
    ///     and still waits for "after" (Hyper-V test, 20/11/2026: it was lost). Once per run.
    /// </summary>
    private void ResumeUnfinishedSession()
    {
        if (_resumeChecked || _session is not null || IsBusy)
            return;
        _resumeChecked = true;
        var pending = sessionStore.LatestUnfinished(timeProvider.GetUtcNow());
        if (pending is null)
            return;
        _session = pending;
        _consent = pending.AiConsent;
        // The box shows the consent already given, keeping its original time.
        _resuming = true;
        try
        {
            AiConsentGiven = pending.AiConsent is not null;
        }
        finally
        {
            _resuming = false;
        }
        ClientLabel = pending.ClientLabel ?? string.Empty;
        ShowWritten(pending.Report);
        ShowSession();
        RefreshCommands();
        StatusText = Loc.Instance[
            "Session.Resumed",
            pending.CreatedAtUtc.ToLocalTime().ToString("g", Loc.CurrentCulture)
        ];
        logger.LogInformation("Unfinished session {Id} resumed", pending.Id);
    }

    /// <summary>Version 1.1.0: the profile that fits this PC (disk, RAM, games).</summary>
    private async Task SuggestProfileAsync()
    {
        try
        {
            Profile.Suggest(await systemInfo.EnsureSnapshotAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not suggest a profile");
        }
    }

    [RelayCommand(CanExecute = nameof(CanMeasureAfter))]
    private Task MeasureAfterAsync() => MeasureAsync(MeasurementPhase.After);

    private bool CanRestartAndContinue() => !IsBusy && _session?.Before is not null && _session.After is null;

    /// <summary>
    ///     Version 1.1.2: restarts the PC and opens Zentra on this page afterwards, where the
    ///     session is picked up for "Medir después".
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestartAndContinue))]
    private async Task RestartAndContinueAsync()
    {
        var answer = await dialogService.ShowSimpleDialogAsync(
            new global::Wpf.Ui.SimpleContentDialogCreateOptions
            {
                Title = Loc.Instance["Session.Restart.Title"],
                Content = Loc.Instance["Session.Restart.Message"],
                PrimaryButtonText = Loc.Instance["Session.Restart.Confirm"],
                CloseButtonText = Loc.Instance["Button.Cancel"],
            }
        );
        if (answer != Wpf.Ui.Controls.ContentDialogResult.Primary)
            return;
        if (_session is not null)
            sessionStore.Save(_session);
        if (!await Task.Run(() => ContinueAfterRestart.ScheduleAndRestart(logger)))
            ErrorText = Loc.Instance["Session.Restart.Failed"];
        else
            StatusText = Loc.Instance["Session.Restart.Scheduled"];
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void NewSession()
    {
        _session = null;
        RefreshSteps();
        AiConsentGiven = false;
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
        : apiKeyStore.IsEmbedded ? Loc.Instance["Session.Ai.Key.Embedded"]
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

    // ----- Consent and the report through files (plan F1-07 and F1-08) ------------------

    private AiConsentRecord? _consent;

    /// <summary>Started with --equipo-tecnico: the key box. Anywhere else, the files.</summary>
    public bool IsTechnicianPc => apiKeyStore.IsTechnicianPc;
    public bool IsClientPc => !IsTechnicianPc;

    /// <summary>The file round trip to the technician's PC, only when this Zentra has no key.</summary>
    public bool ShowsFileExchange => IsClientPc && !apiKeyStore.HasEmbeddedKey;

    /// <summary>
    ///     The client's authorization (Ley 1581). Unchecked, nothing about the session goes to the
    ///     AI or to a file for it; the template writes the report.
    /// </summary>
    [ObservableProperty]
    private bool _aiConsentGiven;

    partial void OnAiConsentGivenChanged(bool value)
    {
        if (_resuming)
            return;
        _consent = value ? AiConsent.Accept(timeProvider.GetUtcNow()) : null;
        if (_session is null)
            return;
        _session = _session with { AiConsent = _consent };
        sessionStore.Save(_session);
    }

    /// <summary>Client's PC: the anonymized data, in the session folder, for the technician's PC.</summary>
    [RelayCommand(CanExecute = nameof(CanGeneratePdf))]
    private async Task ExportAiRequestAsync()
    {
        if (_session is null)
            return;
        ErrorText = null;
        if (!AiConsent.IsGiven(_session))
        {
            ErrorText = Loc.Instance["Session.Ai.Consent.Required"];
            return;
        }
        IsBusy = true;
        try
        {
            _session = _session with { ClientLabel = ClientLabel };
            var payload = await BuildPayloadAsync();
            var folder = SessionStore.FolderFor(_session);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, AiRequestFile.SuggestedName);
            AiExchange.Write(path, AiExchange.Request(_session, payload));
            sessionStore.Save(_session);
            StatusText = Loc.Instance["Session.Ai.Exported", path];
            Services.UI.ActivityLog.Add(StatusText, Services.UI.ActivityKind.Good);
            UnelevatedShell.Open(folder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI request export failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Client's PC: the report the technician's PC wrote, only if it is this session's.</summary>
    [RelayCommand(CanExecute = nameof(CanGeneratePdf))]
    private async Task ImportAiReportAsync()
    {
        if (_session is null)
            return;
        ErrorText = null;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Instance["Session.Ai.FileFilter"],
            FileName = AiResultFile.SuggestedName,
        };
        if (dialog.ShowDialog() != true)
            return;
        IsBusy = true;
        try
        {
            _session = _session with { ClientLabel = ClientLabel };
            var payload = await BuildPayloadAsync();
            var (record, error) = AiExchange.ImportFile(dialog.FileName, _session, payload);
            if (record is null)
            {
                logger.LogWarning("AI report import refused: {Reason}", error);
                ErrorText = Loc.Instance[$"Session.Ai.ImportError.{error}"];
                return;
            }
            _session = _session with { Report = record };
            sessionStore.Save(_session);
            ShowWritten(record);
            StatusText = Loc.Instance["Session.Ai.Imported"];
            Services.UI.ActivityLog.Add(StatusText, Services.UI.ActivityKind.Good);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI report import failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Technician's PC: reads a client's request, writes the report, saves it beside it.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task WriteFromRequestAsync()
    {
        ErrorText = null;
        if (!apiKeyStore.IsTechnicianPc)
            return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Instance["Session.Ai.FileFilter"],
            FileName = AiRequestFile.SuggestedName,
        };
        if (dialog.ShowDialog() != true)
            return;
        var request = AiExchange.ReadRequest(dialog.FileName);
        if (request is null)
        {
            ErrorText = Loc.Instance["Session.Ai.ImportError.WrongFile"];
            return;
        }
        IsBusy = true;
        StatusText = Loc.Instance["Session.Ai.Writing"];
        try
        {
            var record = await Task.Run(() =>
                aiReportService.WriteAsync(request, CancellationToken.None)
            );
            RefreshKey();
            if (record.Source != ReportSource.Ai)
            {
                // A template is written on the client's PC just as well: nothing to hand back.
                StatusText = Loc.Instance[
                    $"Session.Ai.Template.{record.TemplateReason ?? TemplateReason.NoKey}"
                ];
                return;
            }
            var path = Path.Combine(
                Path.GetDirectoryName(dialog.FileName)!,
                AiResultFile.SuggestedName
            );
            AiExchange.Write(path, AiExchange.Result(request, record));
            StatusText = Loc.Instance[
                "Session.Ai.ResultSaved",
                path,
                record.InputTokens ?? 0,
                record.OutputTokens ?? 0
            ];
            UnelevatedShell.Open(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Report from a request file failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<AiPayload> BuildPayloadAsync()
    {
        var model = await pdfReportService.BuildModelAsync(_session!);
        var session = _session!;
        return await Task.Run(() => AiPayloadBuilder.Build(session, model));
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
            // Plan F3-04: the fingerprint to hand over separately, so the client can check the PDF.
            StatusText =
                Loc.Instance["Session.Pdf.Saved", LastPdfPath]
                + " "
                + Loc.Instance["Report.Integrity.Hint", ReportIntegrity.Sha256Of(LastPdfPath)];
            Services.UI.ActivityLog.Add(
                Loc.Instance["Activity.Pdf", Path.GetFileName(LastPdfPath)],
                Services.UI.ActivityKind.Good
            );
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
        // This session's folder once it exists (it is created by "Measure before"); before
        // that, the folder with every earlier session. Never a silent click.
        var folder = _session is null ? null : SessionStore.FolderFor(_session);
        if (folder is not null && Directory.Exists(folder))
            UnelevatedShell.Open(folder);
        else if (Directory.Exists(SessionStore.SessionsDirectory))
            UnelevatedShell.Open(SessionStore.SessionsDirectory);
        else
            StatusText = Loc.Instance["Session.Folder.NotYet"];
    }

    /// <summary>
    ///     Plan F3-06: installed programs and Windows activation, into this session's folder (or a
    ///     dated one under Sessions when there is no session yet). Read-only.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ExportInventoryAsync()
    {
        IsBusy = true;
        ErrorText = null;
        StatusText = Loc.Instance["Inventory.Running"];
        try
        {
            var folder = _session is null
                ? Path.Combine(
                    SessionStore.SessionsDirectory,
                    $"Inventario_{timeProvider.GetLocalNow():yyyy-MM-dd_HHmm}"
                )
                : SessionStore.FolderFor(_session);
            var files = await SoftwareInventory.WriteAsync(folder, logger, CancellationToken.None);
            StatusText = Loc.Instance[
                files.Count > 1 ? "Inventory.Done" : "Inventory.DoneNoWinget",
                folder
            ];
            UnelevatedShell.Open(folder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Inventory export failed");
            ErrorText = Loc.Instance["Session.Error", SecretRedactor.Redact(ex.Message)];
        }
        finally
        {
            IsBusy = false;
        }
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
                AiConsent = _consent,
            };

            // Measure first: the diagnosis uses the RAM at rest (R-RAM-IDLE-HIGH), and running the
            // diagnosis first would load the PC during the rest measurement.
            var measurement = await Task.Run(() =>
                measurementService.MeasureAsync(
                    phase,
                    progress,
                    _cts.Token,
                    _session.Before?.CapturedAtUtc
                )
            );
            StatusText = Loc.Instance["Session.Step.Diagnosis"];
            var report = await Task.Run(() =>
                diagnosticService.RunAsync(
                    measurement.IdleRamPercent,
                    measurement.RandomReadIops,
                    _cts.Token
                )
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
            Services.UI.ActivityLog.Add(
                Loc.Instance[
                    phase == MeasurementPhase.Before
                        ? "Activity.MeasuredBefore"
                        : "Activity.MeasuredAfter"
                ],
                Services.UI.ActivityKind.Good
            );
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
        RefreshSteps();
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
        RestartAndContinueCommand.NotifyCanExecuteChanged();
        GeneratePdfCommand.NotifyCanExecuteChanged();
        WriteReportCommand.NotifyCanExecuteChanged();
        ExportAiRequestCommand.NotifyCanExecuteChanged();
        ImportAiReportCommand.NotifyCanExecuteChanged();
    }
}
