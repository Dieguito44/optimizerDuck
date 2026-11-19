using System.Globalization;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.System;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>Collects a <see cref="DiagnosticSnapshot" /> from the live system.</summary>
public interface IDiagnosticCollector
{
    Task<DiagnosticSnapshot> CollectAsync(CancellationToken ct = default);
}

/// <summary>
///     Live collector. Reuses <see cref="SystemInfoService" /> and the startup manager, and runs
///     the extra probes in parallel, each with its own time limit. A slow or failing probe marks
///     its section and leaves the others intact. Only user cancellation throws.
/// </summary>
public sealed class DiagnosticCollector(
    SystemInfoService systemInfo,
    StartupManagerService startupManager,
    TimeProvider timeProvider,
    ILogger<DiagnosticCollector> logger
) : IDiagnosticCollector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    public async Task<DiagnosticSnapshot> CollectAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var info = await systemInfo.RefreshAsync(ct).ConfigureAwait(false);

        var storageTask = Run(
            "storage",
            DiagnosticProbes.ReadStorage,
            ProbeTimeout,
            new DiagnosticProbes.StorageProbeResult(
                [],
                Measured<int>.Missing(NotMeasuredReason.TimedOut),
                SectionStatus.TimedOut
            ),
            ct
        );
        var stabilityTask = Run(
            "stability",
            () => DiagnosticProbes.ReadStability(now),
            ProbeTimeout,
            new DiagnosticProbes.StabilityProbeResult(
                TimedOut<int>(),
                TimedOut<int>(),
                TimedOut<int>(),
                TimedOut<int>(),
                SectionStatus.TimedOut
            ),
            ct
        );
        var configTask = Run(
            "config",
            DiagnosticProbes.ReadConfig,
            ProbeTimeout,
            new DiagnosticProbes.ConfigProbeResult(
                TimedOut<TcpAutoTuning>(),
                TimedOut<bool>(),
                TimedOut<bool>(),
                TimedOut<bool>(),
                SectionStatus.TimedOut
            ),
            ct
        );
        var activationTask = Run(
            "activation",
            DiagnosticProbes.ReadActivation,
            ProbeTimeout,
            TimedOut<bool>(),
            ct
        );
        var startupTask = CountStartupAppsAsync(ct);
        var bootTask = Run(
            "boot",
            () => Measurements.BootTimeProbe.Read().MedianMs,
            ProbeTimeout,
            TimedOut<int>(),
            ct
        );

        await Task.WhenAll(
                storageTask,
                stabilityTask,
                configTask,
                activationTask,
                startupTask,
                bootTask
            )
            .ConfigureAwait(false);

        var storage = await storageTask.ConfigureAwait(false);
        var stability = await stabilityTask.ConfigureAwait(false);
        var config = await configTask.ConfigureAwait(false);
        var startupApps = await startupTask.ConfigureAwait(false);

        var configStatus =
            config.Status == SectionStatus.Ok && startupApps.HasValue ? SectionStatus.Ok
            : config.Status == SectionStatus.TimedOut ? SectionStatus.TimedOut
            : SectionStatus.Partial;

        var probes = new DiagnosticProbeData
        {
            Disks = storage.Disks,
            SystemDiskNumber = storage.SystemDiskNumber,
            PhysicallyInstalledBytes = DiagnosticProbes.ReadPhysicallyInstalledBytes(),
            Activated = await activationTask.ConfigureAwait(false),
            TcpAutoTuning = config.Tcp,
            TrimEnabled = config.Trim,
            PagefileAutomatic = config.PagefileAutomatic,
            StorageSenseEnabled = config.StorageSense,
            StartupAppsEnabled = startupApps,
            Kp41Count30d = stability.Kp41,
            Kp41PowerButton30d = stability.Kp41PowerButton,
            BugChecks30d = stability.BugChecks,
            Minidumps30d = stability.Minidumps,
            Statuses = new Dictionary<DiagnosticSection, SectionStatus>
            {
                [DiagnosticSection.Disks] = storage.Status,
                [DiagnosticSection.SystemVolume] = info.Storage.Volumes.Any(v => v.IsSystemDrive)
                    ? SectionStatus.Ok
                    : SectionStatus.Failed,
                [DiagnosticSection.Stability] = stability.Status,
                [DiagnosticSection.Config] = configStatus,
            },
        };

        var snapshot = DiagnosticSnapshotBuilder.Build(info, probes, now) with
        {
            BootMedianMs = await bootTask.ConfigureAwait(false),
        };
        logger.LogInformation(
            "Diagnostic snapshot collected: {Disks} disks, {Gpus} GPUs, {Modules} RAM modules",
            snapshot.Disks.Count,
            snapshot.Gpus.Count,
            snapshot.Ram.Modules.Count
        );
        return snapshot;
    }

    private async Task<Measured<int>> CountStartupAppsAsync(CancellationToken ct)
    {
        try
        {
            var apps = await startupManager
                .GetStartupAppsAsync()
                .WaitAsync(StartupTimeout, ct)
                .ConfigureAwait(false);
            return Measured<int>.Of(apps.Count(a => a.IsEnabled));
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Startup app count timed out");
            return Measured<int>.Missing(NotMeasuredReason.TimedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Startup app count failed");
            return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    private async Task<T> Run<T>(
        string name,
        Func<T> probe,
        TimeSpan timeout,
        T onTimeout,
        CancellationToken ct
    )
    {
        try
        {
            return await Task.Run(probe, ct).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The WMI call itself cannot be cancelled; it is abandoned, as elsewhere in the app.
            logger.LogWarning("Diagnostic probe {Probe} timed out after {Timeout}", name, timeout);
            return onTimeout;
        }
    }

    private static Measured<T> TimedOut<T>() => Measured<T>.Missing(NotMeasuredReason.TimedOut);
}

/// <summary>Runs a diagnosis and keeps the last report for the page (and later the PDF and AI).</summary>
public sealed class DiagnosticService(
    IDiagnosticCollector collector,
    ILogger<DiagnosticService> logger
)
{
    public DiagnosticReport? LastReport { get; private set; }

    public async Task<DiagnosticReport> RunAsync(CancellationToken ct = default)
    {
        var snapshot = await collector.CollectAsync(ct).ConfigureAwait(false);
        var report = DiagnosticReport.Create(
            snapshot,
            ReferenceTableLoader.Embedded,
            DiagnosticRules.All,
            logger
        );
        logger.LogInformation(
            "Diagnosis finished: score {Score}, {Findings} findings, {NotMeasurable} rules not measurable",
            report.Score.Score,
            report.Score.Deductions.Count,
            report.Score.RulesNotMeasurable
        );

        // One line per rule so a technician can audit the diagnosis from the log. Evidence is
        // hardware facts only (models, figures, states); the snapshot carries nothing personal.
        foreach (var result in report.Results)
        {
            var evidence = result.Finding is { } finding
                ? string.Join(
                    "; ",
                    finding.Evidence.Select(e =>
                        $"{e.Key}={e.Number?.ToString(CultureInfo.InvariantCulture) ?? e.Text}{(e.Subject is null ? "" : $" [{e.Subject}]")}"
                    )
                )
                : string.Empty;
            logger.LogInformation(
                "Rule {RuleId}: {Outcome} {Severity} {Reason} missing=[{Missing}] {Evidence}",
                result.RuleId,
                result.Outcome,
                result.Finding?.Severity,
                result.Reason,
                string.Join(", ", result.MissingInputs),
                evidence
            );
        }

        LastReport = report;
        return report;
    }
}
