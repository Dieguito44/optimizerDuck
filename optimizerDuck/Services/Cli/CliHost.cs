using System.IO;
using System.Text;
using System.Windows;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;
using optimizerDuck.Services.Reports;
using optimizerDuck.UI.ViewModels.Pages;

namespace optimizerDuck.Services.Cli;

/// <summary>The real <see cref="ICliHost" />: the services the Session page uses.</summary>
public sealed class CliHost(
    OptimizationService optimizationService,
    MeasurementService measurementService,
    DiagnosticService diagnosticService,
    SessionStore sessionStore,
    PdfReportService pdfReportService,
    TimeProvider timeProvider,
    FollowUp.FollowUpService followUp,
    Microsoft.Extensions.Logging.ILogger<CliHost> logger
) : ICliHost
{
    /// <summary>Lines of the preview the confirmation window shows; the full list is in the output.</summary>
    private const int MaxWindowLines = 25;

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public DateTimeOffset Now => timeProvider.GetUtcNow();

    public async Task<bool> ConfirmAsync(
        ServicePreset preset,
        IReadOnlyList<PlannedChange> changes,
        IReadOnlyList<string> apps
    )
    {
        var text = new StringBuilder()
            .AppendLine(Loc.Instance["Cli.Confirm.Message", preset.Name, changes.Count, apps.Count])
            .AppendLine();
        foreach (var change in changes.Take(MaxWindowLines))
            text.AppendLine($"{change.Setting}:{ServiceProfileViewModel.PreviewLine(change)}");
        if (changes.Count > MaxWindowLines)
            text.AppendLine(Loc.Instance["Cli.Confirm.More", changes.Count - MaxWindowLines]);
        if (apps.Count > 0)
            text.AppendLine().AppendLine(string.Join(", ", apps));

        return await Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                text.ToString(),
                Loc.Instance["Profile.Confirm.Title"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No
            ) == MessageBoxResult.Yes
        );
    }

    public async Task<RestorePointResult> CreateRestorePointAsync()
    {
        var result = await optimizationService.CreateRestorePointCoreAsync();
        if (result is RestorePointResult.Success or RestorePointResult.FrequencyLimitReached)
            optimizationService.WasRequestedRestorePoint = true;
        return result;
    }

    public async Task<(Measurement Measurement, DiagnosisSummary Diagnosis)> MeasureAsync(
        MeasurementPhase phase,
        IProgress<string> progress,
        CancellationToken ct,
        DateTimeOffset? bootsAfterUtc = null
    )
    {
        // Measure first, then diagnose: the diagnosis uses the RAM at rest (as the Session page does).
        var lastStep = "";
        var measurement = await Task.Run(
            () =>
                measurementService.MeasureAsync(
                    phase,
                    new SyncProgress<MeasurementProgress>(p =>
                    {
                        // One line per step, not one per second of the countdown.
                        if (p.Step.ToString() == lastStep)
                            return;
                        lastStep = p.Step.ToString();
                        progress.Report(Loc.Instance[$"Session.Step.{p.Step}", p.SecondsLeft]);
                    }),
                    ct,
                    bootsAfterUtc
                ),
            ct
        );
        progress.Report(Loc.Instance["Session.Step.Diagnosis"]);
        var report = await Task.Run(
            () => diagnosticService.RunAsync(measurement.IdleRamPercent, measurement.RandomReadIops, ct),
            ct
        );
        return (measurement, DiagnosisSummary.From(report));
    }

    public (string Folder, string? Client)? NextFollowUp(DateTimeOffset nowLocal) =>
        FollowUp.FollowUpService.NextResultsFolder(nowLocal) is { } folder
            ? (folder, FollowUp.FollowUpService.ReadState()?.ClientLabel)
            : null;

    public Task RecordFollowUpAsync(FollowUp.FollowUpRun run, CancellationToken ct) =>
        followUp.RecordRunAsync(run, ct);

    public Task<bool> RemoveFollowUpAsync(CancellationToken ct) => followUp.RemoveAsync(ct);

    public Task<IReadOnlyList<string>> WriteInventoryAsync(
        ServiceSession session,
        CancellationToken ct
    ) => Reports.SoftwareInventory.WriteAsync(SessionStore.FolderFor(session), logger, ct);

    public async Task<IReadOnlyList<string>> WriteReportAsync(
        ServiceSession session,
        string? reportDirectory,
        string transcript,
        IReadOnlyList<string> extraFiles,
        CancellationToken ct
    )
    {
        // Like the Session page, every PDF carries a written report; here always the template's.
        if (session.Before is not null && session.Report is null)
            session = session with
            {
                Report = Ai.AiReportService.WriteTemplate(
                    session,
                    await pdfReportService.BuildModelAsync(session)
                ),
            };

        sessionStore.Save(session);
        var folder = SessionStore.FolderFor(session);
        var files = new List<string> { Path.Combine(folder, "session.json") };
        if (session.Before is not null)
        {
            files.Add(await pdfReportService.GenerateAsync(session, ct));
            files.Add(Path.Combine(folder, Reports.ReportIntegrity.ManifestName));
        }

        files.AddRange(extraFiles);
        var transcriptPath = Path.Combine(folder, "zentra-cli.txt");
        await File.WriteAllTextAsync(transcriptPath, transcript, Encoding.UTF8, ct);
        files.Add(transcriptPath);

        if (string.IsNullOrWhiteSpace(reportDirectory))
            return files;

        var target = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(target);
        return files
            .Where(File.Exists)
            .Select(file =>
            {
                var copy = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, copy, overwrite: true);
                return copy;
            })
            .ToList();
    }

    /// <summary>Reports on the measuring thread, so the step lines keep their order.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
