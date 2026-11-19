using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.Repair;

/// <summary>The repair steps, in the order the plan runs them.</summary>
public enum RepairStep
{
    ScanHealth,
    RestoreHealth,
    Sfc,
    ChkdskScan,
}

/// <summary>How a repair step ended.</summary>
public enum RepairOutcome
{
    /// <summary>Checked, nothing wrong.</summary>
    Ok,

    /// <summary>Something was repaired.</summary>
    Repaired,

    /// <summary>Not needed (e.g. RestoreHealth on a healthy image).</summary>
    Skipped,

    /// <summary>The tool finished and found a problem it could not fix online.</summary>
    Problem,

    /// <summary>The tool failed or timed out.</summary>
    Failed,
}

/// <summary>State of the Windows component store (Repair-WindowsImage ImageHealthState).</summary>
public enum WindowsImageHealth
{
    Healthy,
    Repairable,
    NonRepairable,
}

/// <summary>What one step did, for the page and the history.</summary>
public sealed record RepairStepResult(
    RepairStep Step,
    RepairOutcome Outcome,
    int ExitCode,
    string Summary,
    TimeSpan Duration
);

/// <summary>
///     "Repair Windows" (plan phase 4): DISM ScanHealth, DISM RestoreHealth, sfc /scannow and
///     chkdsk /scan, in that order, through the app's single execution path so every step is in
///     the history. Repairs cannot be undone; they are recorded as irreversible.
/// </summary>
public sealed class RepairService(ShellService shell, ILogger<RepairService> logger)
{
    /// <summary>Permanent record key of the repair tool. Never change it.</summary>
    public static readonly OperationSubject Subject = new(
        Guid.Parse("B2D4E6F8-1A3C-4E5F-9071-8293A4B5C6D7"),
        "WindowsRepair",
        "Windows repair"
    );

    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(60);
    private const int StepCount = 4;

    /// <summary>Builds the run. <paramref name="results" /> receives each step as it ends.</summary>
    public OperationRequest BuildRequest(ICollection<RepairStepResult> results) =>
        new(
            Subject,
            Loc.Instance["Repair.Header.Title"],
            logger,
            RevertPersistence.Disabled,
            (progress, context) => RunAsync(progress, context, results)
        );

    private async Task<ApplyResult> RunAsync(
        IProgress<ProcessingProgress> progress,
        OptimizationContext context,
        ICollection<RepairStepResult> results
    )
    {
        var ct = context.CancellationToken;

        // 1. DISM ScanHealth, then the language-independent health state it leaves behind.
        var scan = await RunToolAsync(
            RepairStep.ScanHealth,
            "dism.exe",
            "/Online /Cleanup-Image /ScanHealth",
            progress,
            ct
        );
        var health = await ReadImageHealthAsync(ct);
        var scanResult =
            scan.ExitCode != 0 || health is null
                ? Result(RepairStep.ScanHealth, RepairOutcome.Failed, scan)
                : Result(
                    RepairStep.ScanHealth,
                    health == WindowsImageHealth.Healthy ? RepairOutcome.Ok : RepairOutcome.Problem,
                    scan
                );
        Record(context, results, scanResult, "DISM /ScanHealth");

        // 2. DISM RestoreHealth, only when the store is damaged.
        if (health == WindowsImageHealth.Healthy)
        {
            Record(
                context,
                results,
                new RepairStepResult(
                    RepairStep.RestoreHealth,
                    RepairOutcome.Skipped,
                    0,
                    Loc.Instance["Repair.RestoreHealth.NotNeeded"],
                    TimeSpan.Zero
                ),
                "DISM /RestoreHealth"
            );
        }
        else
        {
            var restore = await RunToolAsync(
                RepairStep.RestoreHealth,
                "dism.exe",
                "/Online /Cleanup-Image /RestoreHealth",
                progress,
                ct
            );
            Record(
                context,
                results,
                Result(
                    RepairStep.RestoreHealth,
                    restore.ExitCode == 0 ? RepairOutcome.Repaired : RepairOutcome.Failed,
                    restore
                ),
                "DISM /RestoreHealth"
            );
        }

        // 3. System File Checker.
        var sfc = await RunToolAsync(RepairStep.Sfc, "sfc.exe", "/scannow", progress, ct);
        Record(
            context,
            results,
            Result(
                RepairStep.Sfc,
                sfc.ExitCode == 0 ? RepairOutcome.Repaired : RepairOutcome.Failed,
                sfc
            ),
            "sfc /scannow"
        );

        // 4. Online disk check of the system drive.
        var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var chkdsk = await RunToolAsync(
            RepairStep.ChkdskScan,
            "chkdsk.exe",
            $"{drive} /scan",
            progress,
            ct
        );
        Record(
            context,
            results,
            Result(RepairStep.ChkdskScan, ChkdskOutcome(chkdsk.ExitCode, chkdsk.TimedOut), chkdsk),
            $"chkdsk {drive} /scan"
        );

        return context.Changes.ToApplyResult();
    }

    private async Task<ConsoleRunResult> RunToolAsync(
        RepairStep step,
        string tool,
        string arguments,
        IProgress<ProcessingProgress> progress,
        CancellationToken ct
    )
    {
        var index = (int)step + 1;
        var name = Loc.Instance[$"Repair.Step.{step}"];
        progress.Report(
            new ProcessingProgress
            {
                Message = Loc.Instance["Repair.Progress", index, StepCount, name, "…"],
                IsIndeterminate = true,
            }
        );

        var result = await LongConsoleRunner.RunAsync(
            tool,
            arguments,
            StepTimeout,
            (percent, _) =>
                progress.Report(
                    new ProcessingProgress
                    {
                        Message = Loc.Instance[
                            "Repair.Progress",
                            index,
                            StepCount,
                            name,
                            percent is { } p ? $"{p} %" : "…"
                        ],
                        IsIndeterminate = percent is null,
                        Value = percent ?? 0,
                        Total = 100,
                    }
                ),
            logger,
            ct
        );

        logger.LogInformation(
            "Repair step {Step}: exit {Code}{TimedOut}. {Tail}",
            step,
            result.ExitCode,
            result.TimedOut ? " (timed out)" : "",
            ConsoleOutput.Tail(result.Output)
        );
        return result;
    }

    /// <summary>Reads the component store state left by the last scan. Null if unreadable.</summary>
    private async Task<WindowsImageHealth?> ReadImageHealthAsync(CancellationToken ct)
    {
        var result = await shell.QueryPowerShellAsync(
            "(Repair-WindowsImage -Online -CheckHealth).ImageHealthState",
            logger,
            ct: ct
        );
        return ParseImageHealth(result.Stdout);
    }

    internal static WindowsImageHealth? ParseImageHealth(string? text) =>
        text?.Trim() switch
        {
            "Healthy" => WindowsImageHealth.Healthy,
            "Repairable" => WindowsImageHealth.Repairable,
            "NonRepairable" => WindowsImageHealth.NonRepairable,
            _ => null,
        };

    /// <summary>chkdsk exit codes: 0 clean, 1 fixed, 2 cleanup done, 3 needs an offline repair.</summary>
    internal static RepairOutcome ChkdskOutcome(int exitCode, bool timedOut) =>
        timedOut
            ? RepairOutcome.Failed
            : exitCode switch
            {
                0 => RepairOutcome.Ok,
                1 or 2 => RepairOutcome.Repaired,
                3 => RepairOutcome.Problem,
                _ => RepairOutcome.Failed,
            };

    private static RepairStepResult Result(
        RepairStep step,
        RepairOutcome outcome,
        ConsoleRunResult run
    ) =>
        new(
            step,
            outcome,
            run.ExitCode,
            run.TimedOut ? Loc.Instance["Repair.TimedOut"] : ConsoleOutput.Tail(run.Output),
            TimeSpan.Zero
        );

    /// <summary>Records the step in the history with the kind that matches what it did.</summary>
    private static void Record(
        OpCall call,
        ICollection<RepairStepResult> results,
        RepairStepResult result,
        string command
    )
    {
        results.Add(result);
        var description = string.IsNullOrWhiteSpace(result.Summary)
            ? command
            : $"{command}: {result.Summary}";

        switch (result.Outcome)
        {
            case RepairOutcome.Ok:
            case RepairOutcome.Skipped:
                call.Changes.AddSkip(ServiceStrings.ShellName, description);
                break;
            case RepairOutcome.Repaired:
                call.Changes.AddIrreversible(ServiceStrings.ShellName, description);
                break;
            default:
                call.Changes.Add(
                    ServiceStrings.ShellName,
                    command,
                    false,
                    error: Loc.Instance[$"Repair.Outcome.{result.Outcome}"],
                    errorDetail: result.Summary,
                    nativeErrorCode: result.ExitCode
                );
                break;
        }
    }
}
