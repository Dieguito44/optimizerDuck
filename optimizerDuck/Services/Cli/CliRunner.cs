using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;
using optimizerDuck.UI.ViewModels.Pages;

namespace optimizerDuck.Services.Cli;

/// <summary>What the CLI needs from the machine, so the order of the steps can be tested.</summary>
public interface ICliHost
{
    bool IsElevated { get; }
    DateTimeOffset Now { get; }

    /// <summary>The confirmation window (not with --silent). True = apply.</summary>
    Task<bool> ConfirmAsync(
        ServicePreset preset,
        IReadOnlyList<PlannedChange> changes,
        IReadOnlyList<string> apps
    );

    Task<RestorePointResult> CreateRestorePointAsync();

    /// <summary>The same measurement and diagnosis as "Medir antes" and "Medir después".</summary>
    Task<(Measurement Measurement, DiagnosisSummary Diagnosis)> MeasureAsync(
        MeasurementPhase phase,
        IProgress<string> progress,
        CancellationToken ct
    );

    /// <summary>The folder and client of the next follow-up check, or null when none is due (plan F3-05).</summary>
    (string Folder, string? Client)? NextFollowUp(DateTimeOffset nowLocal);

    Task RecordFollowUpAsync(FollowUp.FollowUpRun run, CancellationToken ct);

    Task<bool> RemoveFollowUpAsync(CancellationToken ct);

    /// <summary>Writes the software inventory into the session folder (plan F3-06).</summary>
    Task<IReadOnlyList<string>> WriteInventoryAsync(ServiceSession session, CancellationToken ct);

    /// <summary>
    ///     Saves session.json (and the PDF when there is a "before"); copies them, the extra files
    ///     and the transcript to the folder.
    /// </summary>
    Task<IReadOnlyList<string>> WriteReportAsync(
        ServiceSession session,
        string? reportDirectory,
        string transcript,
        IReadOnlyList<string> extraFiles,
        CancellationToken ct
    );
}

/// <summary>
///     Plan of improvements F3-02: Zentra without its window. The order is the one the Session page
///     follows, with the same guards: the preset is validated and previewed first, the technician
///     confirms (unless --silent), and nothing is applied without a restore point.
/// </summary>
public sealed class CliRunner(
    ServiceProfileRunner runner,
    ICliHost host,
    TextWriter output,
    ILogger<CliRunner> logger
)
{
    private readonly StringBuilder _transcript = new();

    public async Task<CliExitCode> RunAsync(CliOptions options, CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Say(Loc.Instance["Cli.Cancelled"]);
            return CliExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CLI run failed");
            Say(Loc.Instance["Cli.Failed", SecretRedactor.Redact(ex.Message)]);
            return CliExitCode.Failed;
        }
    }

    private async Task<CliExitCode> RunCoreAsync(CliOptions options, CancellationToken ct)
    {
        if (options.Help)
        {
            Say(Loc.Instance["Cli.Help"]);
            return CliExitCode.Ok;
        }
        if (options.VerifyPath is { } verify)
            return Verify(verify);
        if (!host.IsElevated)
        {
            Say(Loc.Instance["Cli.NotElevated"]);
            return CliExitCode.NotElevated;
        }
        if (options.RemoveFollowUp)
        {
            var removed = await host.RemoveFollowUpAsync(ct);
            Say(Loc.Instance[removed ? "FollowUp.Removed" : "FollowUp.RemoveFailed"]);
            return removed ? CliExitCode.Ok : CliExitCode.Failed;
        }
        if (options.FollowUp)
            return await FollowUpAsync(ct);

        ServicePreset? preset = null;
        if (options.PresetPath is not null)
        {
            preset = await LoadPresetAsync(options);
            if (preset is null)
                return CliExitCode.InvalidPreset;
        }

        var session = new ServiceSession
        {
            CreatedAtUtc = host.Now,
            ClientLabel = options.Client,
            PresetName = preset?.Name,
        };

        if (preset is not null)
        {
            Say(Loc.Instance["Cli.Preset", preset.Name]);
            var changes = (await runner.PreviewAsync(preset, ct))
                .Where(c => c.WillChange)
                .ToList();
            var apps = !options.RemovesApps
                ? []
                : (await runner.AppsToRemoveAsync(preset)).Select(a => a.Name).ToList();

            Say(Loc.Instance["Cli.Preview", changes.Count, apps.Count]);
            foreach (var group in changes.GroupBy(c => c.Setting))
            {
                Say(group.Key);
                foreach (var change in group)
                    Say(ServiceProfileViewModel.PreviewLine(change));
            }
            foreach (var app in apps)
                Say(Loc.Instance["Cli.AppLine", app]);

            if (!options.Silent && !await host.ConfirmAsync(preset, changes, apps))
            {
                Say(Loc.Instance["Cli.Cancelled"]);
                return CliExitCode.Cancelled;
            }

            var restorePoint = await host.CreateRestorePointAsync();
            if (
                restorePoint
                is not (RestorePointResult.Success or RestorePointResult.FrequencyLimitReached)
            )
            {
                Say(Loc.Instance["Cli.RestorePointFailed"]);
                return CliExitCode.RestorePointFailed;
            }
            Say(
                restorePoint == RestorePointResult.Success
                    ? Loc.Instance["Cli.RestorePoint"]
                    : Loc.Instance["Profile.RestorePoint.Recent"]
            );
        }

        if (options.Diagnose)
            session = await MeasureAsync(session, MeasurementPhase.Before, ct);

        ProfileRunResult? result = null;
        if (preset is not null)
        {
            var progress = new SyncProgress<ProcessingProgress>(p => Say(p.Message));
            result = await runner.ApplyAsync(preset, options.RemovesApps, progress, ct);
            Say(
                Loc.Instance[
                    "Profile.Done.Applied",
                    result.Count(ProfileStepOutcome.Applied),
                    result.Count(ProfileStepOutcome.AlreadyOptimal)
                        + result.Count(ProfileStepOutcome.AlreadyApplied),
                    result.Count(ProfileStepOutcome.NotApplicable),
                    result.Count(ProfileStepOutcome.Failed),
                    result.AppsRemoved.Count
                ]
            );
            foreach (var s in result.Settings.Where(s => s.Outcome == ProfileStepOutcome.Failed))
                Say(Loc.Instance["Profile.Problem.Setting", s.Name]);
            foreach (var name in result.AppsFailed)
                Say(Loc.Instance["Profile.Problem.App", name]);

            if (options.Diagnose)
                session = await MeasureAsync(session, MeasurementPhase.After, ct);
        }

        IReadOnlyList<string> inventory = [];
        if (options.Inventory)
        {
            Say(Loc.Instance["Cli.Inventory"]);
            inventory = await host.WriteInventoryAsync(session, ct);
        }

        var files = await host.WriteReportAsync(
            session,
            options.ReportDirectory,
            _transcript.ToString(),
            inventory,
            ct
        );
        foreach (var file in files)
            Say(Loc.Instance["Cli.File", file]);
        // Plan F3-04: the PDF fingerprint, to hand over apart from the PDF.
        foreach (
            var pdf in files.Where(f =>
                f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(f)
            )
        )
            Say(Loc.Instance["Report.Integrity.Hint", Reports.ReportIntegrity.Sha256Of(pdf)]);

        var partial =
            result is not null
            && (result.Count(ProfileStepOutcome.Failed) > 0 || result.AppsFailed.Count > 0);
        Say(partial ? Loc.Instance["Cli.DonePartial"] : Loc.Instance["Cli.Done"]);
        return partial ? CliExitCode.Partial : CliExitCode.Ok;
    }

    /// <summary>
    ///     --followup, from the scheduled task: the diagnosis only (measure and diagnose, no preset,
    ///     no changes) into the follow-up folder, then the run is recorded. The last check removes
    ///     the task.
    /// </summary>
    private async Task<CliExitCode> FollowUpAsync(CancellationToken ct)
    {
        var now = host.Now.ToLocalTime();
        if (host.NextFollowUp(now) is not { } next)
        {
            Say(Loc.Instance["FollowUp.NoneDue"]);
            await host.RemoveFollowUpAsync(ct);
            return CliExitCode.Ok;
        }

        var session = new ServiceSession
        {
            CreatedAtUtc = host.Now,
            ClientLabel = next.Client,
            PresetName = null,
        };
        session = await MeasureAsync(session, MeasurementPhase.Before, ct);
        var files = await host.WriteReportAsync(
            session,
            next.Folder,
            _transcript.ToString(),
            [],
            ct
        );
        foreach (var file in files)
            Say(Loc.Instance["Cli.File", file]);
        await host.RecordFollowUpAsync(
            new FollowUp.FollowUpRun(host.Now, next.Folder, session.DiagnosisBefore?.Score.Score),
            ct
        );
        Say(Loc.Instance["Cli.Done"]);
        return CliExitCode.Ok;
    }

    /// <summary>--verify: every file of ZENTRA-SHA256.txt against its fingerprint. Changes nothing.</summary>
    private CliExitCode Verify(string path)
    {
        var checks = Reports.ReportIntegrity.Verify(path);
        if (checks is null || checks.Count == 0)
        {
            Say(Loc.Instance["Cli.Verify.NoManifest", path]);
            return CliExitCode.VerificationFailed;
        }
        foreach (var check in checks)
            Say(Loc.Instance[$"Cli.Verify.{check.Status}", check.File]);
        var ok = checks.All(c => c.Status == Reports.IntegrityStatus.Ok);
        Say(Loc.Instance[ok ? "Cli.Verify.AllOk" : "Cli.Verify.Failed"]);
        return ok ? CliExitCode.Ok : CliExitCode.VerificationFailed;
    }

    private async Task<ServicePreset?> LoadPresetAsync(CliOptions options)
    {
        ServicePreset preset;
        if (options.UsesStandardPreset)
            preset = ServicePreset.Standard;
        else
        {
            var path = options.PresetPath!;
            if (!File.Exists(path))
            {
                Say(Loc.Instance["Cli.PresetMissing", path]);
                return null;
            }
            var loaded = ServicePresetFile.Load(path, await runner.CatalogAsync());
            if (!loaded.IsValid)
            {
                Say(Loc.Instance["Preset.Invalid", Path.GetFileName(path)]);
                foreach (var problem in loaded.Problems)
                    Say("  " + ServiceProfileViewModel.ProblemText(problem));
                return null;
            }
            preset = loaded.Preset!;
        }

        // Two keys for Experto settings: allowRisky in the file and --allow-risky on the command line.
        var risky = (await runner.SettingsAsync(preset))
            .Where(o => o.Risk == OptimizationRisk.Risky)
            .ToList();
        if (risky.Count > 0 && !options.AllowRisky)
        {
            Say(Loc.Instance["Cli.RiskyNeedsFlag", string.Join(", ", risky.Select(o => o.Name))]);
            return null;
        }
        return preset;
    }

    private async Task<ServiceSession> MeasureAsync(
        ServiceSession session,
        MeasurementPhase phase,
        CancellationToken ct
    )
    {
        Say(
            Loc.Instance[
                phase == MeasurementPhase.Before ? "Cli.MeasureBefore" : "Cli.MeasureAfter"
            ]
        );
        var (measurement, diagnosis) = await host.MeasureAsync(
            phase,
            new SyncProgress<string>(Say),
            ct
        );
        Say(Loc.Instance["Cli.Score", diagnosis.Score.Score]);
        return phase == MeasurementPhase.Before
            ? session with
            {
                Before = measurement,
                DiagnosisBefore = diagnosis,
            }
            : session with
            {
                After = measurement,
                DiagnosisAfter = diagnosis,
            };
    }

    private void Say(string line)
    {
        output.WriteLine(line);
        _transcript.AppendLine(line);
    }

    /// <summary>Reports on the caller's thread, so the output keeps its order.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
