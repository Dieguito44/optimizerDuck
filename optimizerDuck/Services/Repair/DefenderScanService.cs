using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.Repair;

/// <summary>
///     ACT-DEFENDER-FULL and ACT-DEFENDER-OFFLINE (plan phase 6B), with the plan's exact commands,
///     through the app's single execution path so each run is in the history. A scan changes no
///     setting: it is recorded as irreversible (what Defender removes is in its own history).
/// </summary>
public sealed class DefenderScanService(ILogger<DefenderScanService> logger)
{
    /// <summary>Permanent record key of the Defender scan tool. Never change it.</summary>
    public static readonly OperationSubject Subject = new(
        Guid.Parse("C3E8A1F5-6B2D-4F97-8A04-D19E7B5C2F68"),
        "DefenderScan",
        "Defender scan"
    );

    public const string FullScanCommand = "Start-MpScan -ScanType FullScan";
    public const string OfflineScanCommand = "Start-MpWDOScan";

    public OperationRequest BuildFullScanRequest() =>
        Build(Loc.Instance["Defender.Full.Title"], FullScanCommand, TimeSpan.FromHours(4));

    /// <summary>Restarts the PC into the offline scanner: the caller warns first.</summary>
    public OperationRequest BuildOfflineScanRequest() =>
        Build(Loc.Instance["Defender.Offline.Title"], OfflineScanCommand, TimeSpan.FromMinutes(5));

    private OperationRequest Build(string title, string command, TimeSpan timeout) =>
        new(
            Subject,
            title,
            logger,
            RevertPersistence.Disabled,
            async (progress, context) =>
            {
                progress.Report(new ProcessingProgress { Message = title, IsIndeterminate = true });
                var result = await LongConsoleRunner.RunAsync(
                    "powershell.exe",
                    $"-NoProfile -NonInteractive -Command \"{command}\"",
                    timeout,
                    null,
                    logger,
                    context.CancellationToken
                );
                logger.LogInformation(
                    "{Command}: exit {Code}{TimedOut}",
                    command,
                    result.ExitCode,
                    result.TimedOut ? " (timed out)" : ""
                );
                if (result.ExitCode == 0 && !result.TimedOut)
                    context.Changes.AddIrreversible(ServiceStrings.ShellName, command);
                else
                    context.Changes.Add(
                        ServiceStrings.ShellName,
                        command,
                        false,
                        error: result.TimedOut
                            ? Loc.Instance["Defender.TimedOut"]
                            : Loc.Instance["Defender.Failed", result.ExitCode],
                        errorDetail: ConsoleOutput.Tail(result.Output),
                        nativeErrorCode: result.ExitCode
                    );
                return context.Changes.ToApplyResult();
            }
        );
}
