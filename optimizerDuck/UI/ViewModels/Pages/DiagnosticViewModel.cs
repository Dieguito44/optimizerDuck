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
    ILogger<DiagnosticViewModel> logger
) : ViewModel
{
    private CancellationTokenSource? _cts;

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

    /// <summary>The AI report and PDF arrive in phase 5; the button stays visible but disabled.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateReport))]
    private static void GenerateReport() { }

    private static bool CanGenerateReport() => false;

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

    private static Brush BrushForScore(int score) =>
        score switch
        {
            >= 85 => Frozen(0x2E, 0xA0, 0x43),
            >= 60 => Frozen(0xD9, 0x8E, 0x04),
            _ => Frozen(0xD1, 0x34, 0x38),
        };

    private static Brush BrushForSeverity(Severity severity) =>
        severity switch
        {
            Severity.Critical => Frozen(0xD1, 0x34, 0x38),
            Severity.High => Frozen(0xE0, 0x6C, 0x00),
            Severity.Medium => Frozen(0xD9, 0x8E, 0x04),
            Severity.Low => Frozen(0x2B, 0x88, 0xD8),
            _ => Frozen(0x7A, 0x7A, 0x7A),
        };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
