using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Services.Measurements;

/// <summary>What the measurement is doing now, for the page.</summary>
public enum MeasurementStep
{
    Boot,
    WaitingForIdle,
    IdleRam,
    Counts,
    DiskBenchmark,
    Done,
}

/// <summary>Progress of a measurement. <see cref="SecondsLeft" /> counts down waits.</summary>
public sealed record MeasurementProgress(MeasurementStep Step, int SecondsLeft = 0);

/// <summary>
///     Takes one "before" or "after" measurement (plan phase 3). Read-only, except the
///     benchmark's temporary test file, which is deleted when it ends.
/// </summary>
public sealed class MeasurementService(
    StartupManagerService startupManager,
    DiskBenchmark diskBenchmark,
    TimeProvider timeProvider,
    ILogger<MeasurementService> logger
)
{
    /// <summary>Idle RAM is measured at least this long after the desktop started.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(2);

    /// <param name="bootsAfterUtc">
    ///     For "Medir después": when "Medir antes" ran. Only later boots are timed.
    /// </param>
    public async Task<Measurement> MeasureAsync(
        MeasurementPhase phase,
        IProgress<MeasurementProgress>? progress,
        CancellationToken ct,
        DateTimeOffset? bootsAfterUtc = null
    )
    {
        var captured = timeProvider.GetUtcNow();

        progress?.Report(new(MeasurementStep.Boot));
        var after = phase == MeasurementPhase.After ? bootsAfterUtc?.UtcDateTime : null;
        var boot = await Task.Run(() => BootTimeProbe.Read(after), ct).ConfigureAwait(false);

        var busy = new HashSet<string>(BackgroundWorkProbe.RunningNow());
        await WaitForIdleAsync(progress, ct).ConfigureAwait(false);
        progress?.Report(new(MeasurementStep.IdleRam, (int)IdleWindow.TotalSeconds));
        var cpuStart = CpuTimes.Read();
        var (idleUsed, idlePercent) = await SampleIdleRamAsync(progress, ct).ConfigureAwait(false);
        var idleCpu = CpuTimes.BusyPercent(cpuStart, CpuTimes.Read());
        busy.UnionWith(BackgroundWorkProbe.RunningNow());

        progress?.Report(new(MeasurementStep.Counts));
        var startupApps = await CountStartupAppsAsync(ct).ConfigureAwait(false);
        var processes = SafeCount(() => Process.GetProcesses().Length);
        var services = Measured.FromNullable(
            WmiHelper.Query(
                "SELECT Name FROM Win32_Service WHERE State='Running'",
                static items => (int?)items.Count
            )
        );

        progress?.Report(new(MeasurementStep.DiskBenchmark, 34));
        var bench = await diskBenchmark.RunAsync(ct).ConfigureAwait(false);

        busy.UnionWith(BackgroundWorkProbe.RunningNow());
        busy.UnionWith(
            BackgroundWorkProbe.InstalledBetween(
                captured.UtcDateTime,
                timeProvider.GetUtcNow().UtcDateTime
            )
        );

        progress?.Report(new(MeasurementStep.Done));
        var measurement = new Measurement
        {
            Phase = phase,
            CapturedAtUtc = captured,
            FastStartupEnabled = ReadFastStartup(),
            BootMedianMs = boot.MedianMs,
            BootSource = boot.Source,
            BootDesktopMs = boot.DesktopMs,
            BootAfterDesktopMs = boot.AfterDesktopMs,
            BootSamples = boot.Samples,
            BootCulprits = boot.Culprits,
            IdleRamUsedBytes = idleUsed,
            IdleRamPercent = idlePercent,
            IdleCpuPercent = idleCpu,
            SystemFreeBytes = ReadSystemFreeBytes(),
            StartupAppsEnabled = startupApps,
            ProcessCount = processes,
            RunningServices = services,
            SequentialReadMiBps = bench.SequentialReadMiBps,
            RandomReadIops = bench.RandomReadIops,
            BackgroundWork = [.. busy.Order()],
        };
        if (busy.Count > 0)
            logger.LogInformation(
                "Measurement {Phase}: Windows was installing meanwhile ({Work})",
                phase,
                string.Join(", ", busy)
            );

        logger.LogInformation(
            "Measurement {Phase}: boot {Boot} ms ({Source}, {Samples} boots), idle RAM {Ram} ({Percent} %), "
                + "free C: {Free}, startup {Startup}, processes {Processes}, services {Services}, "
                + "seq read {Seq} MiB/s, random read {Iops} IOPS",
            phase,
            measurement.BootMedianMs,
            measurement.BootSource,
            measurement.BootSamples,
            measurement.IdleRamUsedBytes,
            measurement.IdleRamPercent,
            measurement.SystemFreeBytes,
            measurement.StartupAppsEnabled,
            measurement.ProcessCount,
            measurement.RunningServices,
            measurement.SequentialReadMiBps,
            measurement.RandomReadIops
        );
        return measurement;
    }

    /// <summary>Waits until the desktop has been up for <see cref="IdleDelay" />.</summary>
    private async Task WaitForIdleAsync(
        IProgress<MeasurementProgress>? progress,
        CancellationToken ct
    )
    {
        var remaining = IdleDelay - SinceDesktopStarted();
        while (remaining > TimeSpan.Zero)
        {
            progress?.Report(
                new(MeasurementStep.WaitingForIdle, (int)Math.Ceiling(remaining.TotalSeconds))
            );
            var step = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            await Task.Delay(step, timeProvider, ct).ConfigureAwait(false);
            remaining -= step;
        }
    }

    /// <summary>Time since the user's shell started (explorer.exe), or since boot without it.</summary>
    private TimeSpan SinceDesktopStarted()
    {
        try
        {
            var shells = Process.GetProcessesByName("explorer");
            try
            {
                var start = shells.Select(p => p.StartTime.ToUniversalTime()).Min();
                return timeProvider.GetUtcNow().UtcDateTime - start;
            }
            finally
            {
                foreach (var shell in shells)
                    shell.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return TimeSpan.FromMilliseconds(Environment.TickCount64);
        }
    }

    /// <summary>Whole-PC CPU time (GetSystemTimes), to measure the CPU at rest (version 1.2.0).</summary>
    internal readonly record struct CpuTimes(long Idle, long Kernel, long User)
    {
        [global::System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [return: global::System.Runtime.InteropServices.MarshalAs(
            global::System.Runtime.InteropServices.UnmanagedType.Bool
        )]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public static CpuTimes? Read() =>
            GetSystemTimes(out var idle, out var kernel, out var user)
                ? new CpuTimes(idle, kernel, user)
                : null;

        /// <summary>Busy share between two readings. Kernel time includes the idle time.</summary>
        public static Measured<double> BusyPercent(CpuTimes? start, CpuTimes? end)
        {
            if (start is not { } a || end is not { } b)
                return Measured<double>.Missing(NotMeasuredReason.QueryFailed);
            var total = (b.Kernel - a.Kernel) + (b.User - a.User);
            if (total <= 0)
                return Measured<double>.Missing(NotMeasuredReason.QueryFailed);
            var busy = total - (b.Idle - a.Idle);
            return Measured<double>.Of(Math.Round(Math.Clamp(busy * 100.0 / total, 0, 100), 1));
        }
    }

    private async Task<(Measured<long>, Measured<double>)> SampleIdleRamAsync(
        IProgress<MeasurementProgress>? progress,
        CancellationToken ct
    )
    {
        var used = new List<long>();
        var total = 0L;
        var samples = (int)(IdleWindow / SampleEvery);
        for (var i = 0; i < samples; i++)
        {
            if (NativeMemory.GetMemoryStatus() is { } status && status.ullTotalPhys > 0)
            {
                total = (long)status.ullTotalPhys;
                used.Add((long)(status.ullTotalPhys - status.ullAvailPhys));
            }
            progress?.Report(
                new(MeasurementStep.IdleRam, (int)((samples - i) * SampleEvery.TotalSeconds))
            );
            await Task.Delay(SampleEvery, timeProvider, ct).ConfigureAwait(false);
        }

        if (used.Count == 0 || total == 0)
            return (
                Measured<long>.Missing(NotMeasuredReason.QueryFailed),
                Measured<double>.Missing(NotMeasuredReason.QueryFailed)
            );

        var average = (long)used.Average();
        return (
            Measured<long>.Of(average),
            Measured<double>.Of(Math.Round(average * 100.0 / total, 1))
        );
    }

    private async Task<Measured<int>> CountStartupAppsAsync(CancellationToken ct)
    {
        try
        {
            var apps = await startupManager
                .GetStartupAppsAsync()
                .WaitAsync(TimeSpan.FromSeconds(20), ct)
                .ConfigureAwait(false);
            return Measured<int>.Of(apps.Count(a => a.IsEnabled));
        }
        catch (TimeoutException)
        {
            return Measured<int>.Missing(NotMeasuredReason.TimedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Startup app count failed");
            return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    private static Measured<int> SafeCount(Func<int> count)
    {
        try
        {
            return Measured<int>.Of(count());
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    private static Measured<long> ReadSystemFreeBytes()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            return Measured<long>.Of(drive.AvailableFreeSpace);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Measured<long>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    /// <summary>HiberbootEnabled = 1 means Fast Startup is on.</summary>
    private static Measured<bool> ReadFastStartup()
    {
        if (
            !RegistryService.TryReadValue(
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                    "HiberbootEnabled"
                ),
                out var value
            )
        )
            return Measured<bool>.Missing(NotMeasuredReason.QueryFailed);
        return value switch
        {
            int i => Measured<bool>.Of(i == 1),
            null => Measured<bool>.Missing(NotMeasuredReason.NotReported),
            _ => Measured<bool>.Missing(NotMeasuredReason.NotReported),
        };
    }
}
