using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.History;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Services.Optimization;

/// <summary>
///     Runs the lifecycle every system-changing run takes: it invokes the work, classifies the
///     outcome from the recorded steps, writes the record, and persists revert data when asked.
/// </summary>
public class OperationRunner(
    RevertManager revertManager,
    SystemInfoService systemInfoService,
    StreamService streamService,
    ShellService shellService,
    PowerPlanService powerPlanService,
    ILogger<OperationRunner> logger
)
{
    private readonly ILogger _logger = logger;

    /// <summary>Executes one run and writes what it did to the record store.</summary>
    /// <param name="request">The run to execute.</param>
    /// <param name="progress">The progress reporter the run writes to.</param>
    /// <param name="cancellationToken">The token that cancels the run.</param>
    /// <returns>The classified outcome of the run.</returns>
    public async Task<OptimizationResult> RunAsync(
        OperationRequest request,
        IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var changes = new ChangeSet();
        // Plan F1-03: every change with a way back reaches the disk as soon as it is made.
        var journaled = request.Revert == RevertPersistence.Enabled;
        if (journaled)
        {
            IntentJournal.Begin(request.Subject.Id, request.Subject.Key, DateTimeOffset.UtcNow);
            changes.Recorded = change => IntentJournal.Record(request.Subject.Id, change);
        }

        ActivityLog.Add(Loc.Instance["Activity.Started", request.DisplayName]);
        var result = await RunCoreAsync(request, changes, progress, cancellationToken)
            .ConfigureAwait(false);

        // The run's revert data is saved (or there was none): the journal is no longer needed.
        // When saving failed, the journal stays and the next start recovers it.
        if (journaled && !changes.RevertSaveFailed)
            IntentJournal.Complete(request.Subject.Id);

        // Best effort, after the result is decided: the record must never change what a run
        // reports about the machine.
        var record = ChangeRecord.From(changes, request.Subject, result.Status.ToString());
        ChangeRecordStore.TryWrite(record, _logger);

        // The activity log at the bottom of the window (redesign): one line per run. A failure
        // says why (version 1 test, 20/11/2026: "falló" alone left nothing to go on).
        ActivityLog.Add(
            ActivityLine(result, request.DisplayName, record.ChangedCount),
            result.Status switch
            {
                OptimizationSuccessResult.Success => ActivityKind.Good,
                OptimizationSuccessResult.NothingToDo => ActivityKind.Info,
                OptimizationSuccessResult.PartialSuccess => ActivityKind.Warning,
                _ => ActivityKind.Error,
            }
        );

        return result;
    }

    internal static string ActivityLine(OptimizationResult result, string name, int changed)
    {
        var line = Loc.Instance[$"Activity.{result.Status}", name, changed];
        if (
            result.Status
            is not (OptimizationSuccessResult.Failed or OptimizationSuccessResult.PartialSuccess)
        )
            return line;
        var why = result
            .FailedSteps.Select(f => f.Error)
            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
        if (string.IsNullOrWhiteSpace(why))
            why = result.Message;
        return string.IsNullOrWhiteSpace(why) || line.Contains(why, StringComparison.Ordinal)
            ? line
            : $"{line} ({why.Trim().TrimEnd('.')})";
    }

    private async Task<OptimizationResult> RunCoreAsync(
        OperationRequest request,
        ChangeSet changes,
        IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation(
            "Starting run of {Name} ({Key}) with ID {Id}",
            request.Subject.LogName,
            request.Subject.Key,
            request.Subject.Id
        );

        progress.Report(
            new ProcessingProgress
            {
                Message = Loc.Instance["Optimization.Apply.Processing"],
                IsIndeterminate = true,
            }
        );

        string? providerError = null;
        Exception? exception = null;
        try
        {
            // The PC's facts (RAM, GPU, disks) must be loaded before a setting reads them: with
            // the empty snapshot, a setting applied before the first scan finished saw 0 KB of RAM
            // and failed ("Invalid RAM: 0", version 1 test, 20/11/2026).
            var snapshot = await systemInfoService
                .EnsureSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            var applyResult = await request
                .Work(
                    progress,
                    NewContext(changes, request.RunLogger, snapshot, cancellationToken)
                )
                .ConfigureAwait(false);

            providerError = string.IsNullOrWhiteSpace(applyResult.ErrorMessage)
                ? null
                : applyResult.ErrorMessage;
        }
        catch (OperationCanceledException ocex)
        {
            _logger.LogInformation("Operation {Name} was cancelled", request.Subject.LogName);
            // What changed before the cancel still needs its way back (plan F1-03).
            await TrySaveRevertDataAsync(changes, request).ConfigureAwait(false);
            return new OptimizationResult
            {
                Status = changes.ModifiedSystem
                    ? OptimizationSuccessResult.PartialSuccess
                    : OptimizationSuccessResult.Failed,
                Message = Loc.Instance["Common.OperationCancelled"],
                Exception = ocex,
                FailedSteps = changes.FailedSteps.OrderBy(s => s.Index).ToList(),
            };
        }
        catch (Exception ex)
        {
            exception = ex;
            RecordRunFailure(changes, request, ex);
        }

        if (exception == null && providerError == null)
        {
            progress.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance["Optimization.Apply.Completed"],
                    IsIndeterminate = false,
                    Value = 1,
                    Total = 1,
                }
            );
        }

        ReportUncompensatedChanges(changes, request);

        // Persist before the result, so a persistence failure lands as a failed step.
        await TrySaveRevertDataAsync(changes, request).ConfigureAwait(false);

        var failedSteps = changes.FailedSteps.OrderBy(s => s.Index).ToList();

        // Whether any step succeeded; the all-failed branch below asks this, not the outcome.
        var changedSomething = changes.HasSuccessfulSteps || changes.DidApplyAnything;

        if (exception != null)
        {
            return new OptimizationResult
            {
                Status = changes.ModifiedSystem
                    ? OptimizationSuccessResult.PartialSuccess
                    : OptimizationSuccessResult.Failed,
                Message = Loc.Instance["Optimization.Apply.Error.Failed", request.DisplayName],
                Exception = exception,
                FailedSteps = failedSteps,
            };
        }

        if (providerError != null)
        {
            _logger.LogWarning(
                "Provider error in run {OptimizationKey}: {ProviderError}",
                request.Subject.Key,
                providerError
            );

            var message =
                failedSteps.Count > 0
                    ? Loc.Instance[
                        "Optimization.Apply.Error.FailedWithSteps",
                        request.DisplayName,
                        failedSteps.Count
                    ]
                    : providerError;

            return new OptimizationResult
            {
                Status = changes.ModifiedSystem
                    ? OptimizationSuccessResult.PartialSuccess
                    : OptimizationSuccessResult.Failed,
                Message = message,
                FailedSteps = failedSteps,
            };
        }

        // Every step failed and none carries compensation, so nothing changed: a total failure. A
        // failed step that recorded what it changed falls through to partial success below.
        if (changes.Changes.Count > 0 && !changedSomething)
        {
            return new OptimizationResult
            {
                Status = OptimizationSuccessResult.Failed,
                Message = Loc.Instance["Optimization.Apply.Error.Failed", request.DisplayName],
                FailedSteps = failedSteps,
            };
        }

        // Nothing to do: no step modified the system, because there was nothing to change or
        // every step was a skip. An irreversible step counts as modifying it.
        if (!changes.ModifiedSystem)
        {
            if (changes.Changes.Count == 0)
                _logger.LogWarning(
                    "Run {OptimizationKey} recorded no step at all; the provider may have returned without recording",
                    request.Subject.Key
                );

            _logger.LogInformation(
                "Run {OptimizationKey} changed nothing, reporting nothing to do",
                request.Subject.Key
            );
            return new OptimizationResult
            {
                Status = OptimizationSuccessResult.NothingToDo,
                Message = Loc.Instance["Optimization.Apply.NothingToDo", request.DisplayName],
                FailedSteps = failedSteps,
            };
        }

        var failedCount = failedSteps.Count;
        return new OptimizationResult
        {
            Status =
                failedCount == 0
                    ? OptimizationSuccessResult.Success
                    : OptimizationSuccessResult.PartialSuccess,
            Message =
                failedCount == 0
                    ? Loc.Instance["Optimization.Apply.Success", request.DisplayName]
                    : Loc.Instance[
                        "Optimization.Apply.Error.FailedWithSteps",
                        request.DisplayName,
                        failedCount
                    ],
            FailedSteps = failedSteps,
        };
    }

    private OptimizationContext NewContext(
        ChangeSet changes,
        ILogger runLogger,
        SystemInfo snapshot,
        CancellationToken cancellationToken
    )
    {
        return new OptimizationContext
        {
            Changes = changes,
            Logger = runLogger,
            CancellationToken = cancellationToken,
            Snapshot = snapshot,
            StreamService = streamService,
            Shell = shellService,
            PowerPlans = powerPlanService,
        };
    }

    /// <summary>
    ///     Reports every step that claims the system was modified while carrying no compensation,
    ///     which is a provider defect.
    /// </summary>
    private void ReportUncompensatedChanges(ChangeSet changes, OperationRequest request)
    {
        foreach (var defect in changes.UncompensatedChanges)
            request.RunLogger.LogWarning(
                "Recorded change {Step} for {Name} carries no revert data, so undo does not cover it",
                defect.Name,
                request.Subject.Key
            );
    }

    private async Task TrySaveRevertDataAsync(ChangeSet changes, OperationRequest request)
    {
        if (request.Revert != RevertPersistence.Enabled || !changes.DidApplyAnything)
            return;

        try
        {
            await revertManager
                .SaveRevertDataAsync(
                    changes,
                    request.Subject.Id,
                    request.Subject.Key,
                    // CancellationToken.None: persist partial work even when the run was cancelled.
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            changes.RevertSaveFailed = true;
            _logger.LogError(ex, "Failed to save revert data for {Name}", request.Subject.Key);
            RecordRunFailure(changes, request, ex);
        }
    }

    /// <summary>
    ///     Records a failure of the run itself as a step, named for the user and described by the
    ///     run's English key, which is what the log reads.
    /// </summary>
    private static void RecordRunFailure(
        ChangeSet changes,
        OperationRequest request,
        Exception exception
    )
    {
        changes.Add(
            request.DisplayName,
            request.Subject.Key,
            false,
            null,
            exception.Message,
            exception.ToString()
        );
    }
}
