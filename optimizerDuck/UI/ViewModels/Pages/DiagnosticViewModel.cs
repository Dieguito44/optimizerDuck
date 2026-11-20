using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;

namespace optimizerDuck.UI.ViewModels.Pages;

/// <summary>One finding card on the diagnosis page.</summary>
public sealed record FindingItem(FindingView View, Brush SeverityBrush)
{
    /// <summary>The severity color, faint, behind the severity label.</summary>
    public Brush SeverityTintBrush { get; } = UI.Styles.ZentraBrushes.Tint(SeverityBrush);

    public string Title => View.Title;
    public string Description => View.Description;
    public string SeverityText => View.SeverityText;
    public string PointsText =>
        View.Points > 0 ? Loc.Instance["Diagnostic.Finding.Points", View.Points] : string.Empty;
    public string EvidenceText => string.Join(Environment.NewLine, View.Evidence);
    public bool HasActions => View.Actions.Count > 0;
    public string ActionsText =>
        string.Join(Environment.NewLine, View.Actions.Select(a => $"• {a}"));
}

/// <summary>
///     Diagnosis page. Read-only: it never changes the PC. The scan only starts when the
///     technician presses the button (the client must authorize it first, plan section 9).
/// </summary>
public partial class DiagnosticViewModel(
    DiagnosticService diagnosticService,
    TemperatureViewModel temperature,
    Wpf.Ui.INavigationService navigationService,
    ILogger<DiagnosticViewModel> logger
) : ViewModel
{
    private CancellationTokenSource? _cts;

    /// <summary>The "Measure temperature" card (phase 6B).</summary>
    public TemperatureViewModel Temperature { get; } = temperature;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyCanExecuteChangedFor(nameof(RunDiagnosticCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFindings))]
    private bool _hasReport;

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private Brush _scoreBrush = Brushes.Gray;

    [ObservableProperty]
    private string _coverageText = string.Empty;

    [ObservableProperty]
    private string _capturedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    public bool HasError => ErrorText is not null;

    [ObservableProperty]
    private bool _hasNotMeasured;

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public ObservableCollection<NotMeasuredView> NotMeasured { get; } = [];

    public bool CanRun => !IsRunning;

    public bool HasNoFindings => HasReport && Findings.Count == 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunDiagnosticAsync()
    {
        IsRunning = true;
        ErrorText = null;
        _cts = new CancellationTokenSource();
        try
        {
            var report = await Task.Run(() => diagnosticService.RunAsync(_cts.Token));
            Show(report);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Diagnosis cancelled by the user");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Diagnosis failed");
            ErrorText = Loc.Instance["Diagnostic.Error", ex.Message];
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>The AI report and the client PDF are made on the Session page.</summary>
    [RelayCommand]
    private void GoToSession() => navigationService.Navigate(typeof(UI.Pages.SessionPage));

    private void Show(DiagnosticReport report)
    {
        Score = report.Score.Score;
        ScoreBrush = BrushForScore(report.Score.Score);
        CoverageText = Loc.Instance[
            "Diagnostic.Score.Coverage",
            report.Score.RulesEvaluated,
            report.Score.RulesNotMeasurable
        ];
        CapturedText = Loc.Instance[
            "Diagnostic.Captured",
            report.Snapshot.CapturedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        ];

        Findings.Clear();
        foreach (var finding in report.Findings)
        {
            var view = FindingTextRenderer.Render(finding);
            Findings.Add(new FindingItem(view, BrushForSeverity(finding.Severity)));
        }

        NotMeasured.Clear();
        foreach (var result in report.Results.Where(r => r.Outcome == RuleOutcome.NotMeasurable))
            NotMeasured.Add(FindingTextRenderer.RenderNotMeasured(result));

        HasNotMeasured = NotMeasured.Count > 0;
        HasReport = true;
        OnPropertyChanged(nameof(HasNoFindings));
    }

    private static Brush BrushForScore(int score) => UI.Styles.ZentraBrushes.ForScore(score);

    private static Brush BrushForSeverity(Severity severity) =>
        UI.Styles.ZentraBrushes.ForSeverity(severity);
}
