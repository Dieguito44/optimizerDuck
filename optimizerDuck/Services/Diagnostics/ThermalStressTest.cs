using System.Diagnostics.Eventing.Reader;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace optimizerDuck.Services.Diagnostics;

public enum StressOutcome
{
    /// <summary>The whole test ran below the limit.</summary>
    Completed,

    /// <summary>Stopped because the CPU reached the limit.</summary>
    StoppedHot,

    /// <summary>Not started, or stopped, because the laptop is (or went) on battery.</summary>
    OnBattery,

    /// <summary>No temperature sensor, or it stopped answering: never a load without a reading.</summary>
    NoSensor,

    Cancelled,
}

public sealed record StressResult(
    StressOutcome Outcome,
    double? IdleC,
    double? MaxC,
    TimeSpan Ran,
    int? ThrottleIncidents
);

/// <summary>What the test needs from the machine, so its safety rules can be tested.</summary>
public interface IStressMachine
{
    /// <summary>The hottest CPU package temperature now, or null when it cannot be read.</summary>
    double? ReadCelsius();

    /// <summary>True on the charger, false on battery, null when the PC has no battery or it is unknown.</summary>
    bool? OnAcPower();

    bool IsLaptop { get; }

    void StartLoad();
    void StopLoad();

    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    /// <summary>Firmware throttling incidents (event 37) since the given time.</summary>
    int? ThrottleIncidentsSince(DateTime utc);
}

/// <summary>
///     Plan of improvements F4-02: a short CPU load test (Moderate: heat and battery). It loads
///     every core for a short time while reading the temperature every second, and stops at once
///     when the CPU reaches the limit, when the laptop leaves the charger, or when the sensor stops
///     answering for 3 seconds. It never starts without a reading or on battery. It changes no
///     setting. The result helps confirm firmware throttling (event 37, audit Z-12).
/// </summary>
public sealed class ThermalStressTest(IStressMachine machine, ILogger logger)
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(90);
    public const double DefaultLimitC = 95;
    private const int MaxMissedReadings = 3;

    public async Task<StressResult> RunAsync(
        TimeSpan duration,
        double limitC,
        IProgress<(int Seconds, double Celsius)>? progress,
        CancellationToken ct
    )
    {
        if (machine.IsLaptop && machine.OnAcPower() is not true)
            return new(StressOutcome.OnBattery, null, null, TimeSpan.Zero, null);

        // A few seconds at rest first: the starting point, and proof the sensor answers.
        var idle = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            if (machine.ReadCelsius() is { } t)
                idle.Add(t);
            await machine.DelayAsync(TimeSpan.FromSeconds(1), ct);
        }
        if (idle.Count == 0)
            return new(StressOutcome.NoSensor, null, null, TimeSpan.Zero, null);
        idle.Sort();
        var idleC = idle[idle.Count / 2];
        if (idleC >= limitC)
            return new(StressOutcome.StoppedHot, idleC, idleC, TimeSpan.Zero, null);

        var started = DateTime.UtcNow;
        double? max = null;
        var missed = 0;
        var seconds = 0;
        var outcome = StressOutcome.Completed;
        machine.StartLoad();
        try
        {
            while (seconds < duration.TotalSeconds)
            {
                await machine.DelayAsync(TimeSpan.FromSeconds(1), ct);
                seconds++;

                if (machine.IsLaptop && machine.OnAcPower() is not true)
                {
                    outcome = StressOutcome.OnBattery;
                    break;
                }

                if (machine.ReadCelsius() is not { } celsius)
                {
                    if (++missed >= MaxMissedReadings)
                    {
                        outcome = StressOutcome.NoSensor;
                        break;
                    }
                    continue;
                }
                missed = 0;
                max = Math.Max(max ?? celsius, celsius);
                progress?.Report((seconds, celsius));
                if (celsius >= limitC)
                {
                    outcome = StressOutcome.StoppedHot;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome = StressOutcome.Cancelled;
        }
        finally
        {
            machine.StopLoad();
        }

        var result = new StressResult(
            outcome,
            Math.Round(idleC, 1),
            max is { } m ? Math.Round(m, 1) : null,
            TimeSpan.FromSeconds(seconds),
            machine.ThrottleIncidentsSince(started)
        );
        logger.LogInformation(
            "[STRESS] {Outcome}: idle {Idle} °C, max {Max} °C after {Seconds} s, throttling incidents {Throttle}",
            result.Outcome,
            result.IdleC,
            result.MaxC,
            seconds,
            result.ThrottleIncidents
        );
        return result;
    }
}

/// <summary>The real machine: LibreHardwareMonitor sensors, one busy thread per logical processor.</summary>
public sealed class StressMachine : IStressMachine, IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true };
    private readonly List<Thread> _threads = [];
    private volatile bool _loading;

    public StressMachine(bool isLaptop)
    {
        IsLaptop = isLaptop;
        _computer.Open();
    }

    public bool IsLaptop { get; }

    public double? ReadCelsius() => ThermalService.ReadPackageCelsius(_computer);

    public bool? OnAcPower() => PowerStatus.OnAcPower();

    public void StartLoad()
    {
        _loading = true;
        for (var i = 0; i < Environment.ProcessorCount; i++)
        {
            var thread = new Thread(Spin) { IsBackground = true, Name = $"Zentra stress {i}" };
            _threads.Add(thread);
            thread.Start();
        }
    }

    public void StopLoad()
    {
        _loading = false;
        foreach (var thread in _threads)
            thread.Join(TimeSpan.FromSeconds(2));
        _threads.Clear();
    }

    private void Spin()
    {
        var x = 1.0001;
        while (_loading)
            for (var i = 0; i < 100_000; i++)
                x = Math.Sqrt(x * x + i) % 1000 + 1.0001;
        GC.KeepAlive(x);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public int? ThrottleIncidentsSince(DateTime utc)
    {
        try
        {
            var query = new EventLogQuery(
                "System",
                PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37) and TimeCreated[@SystemTime>='{utc:o}']]]"
            );
            using var reader = new EventLogReader(query);
            var times = new List<DateTime>();
            while (reader.ReadEvent() is { } e)
                using (e)
                    if (e.TimeCreated is { } time)
                        times.Add(time);
            return ThermalService.CountIncidents(times);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        StopLoad();
        _computer.Close();
    }
}
