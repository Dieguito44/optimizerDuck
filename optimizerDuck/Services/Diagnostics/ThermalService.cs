using System.Diagnostics.Eventing.Reader;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using optimizerDuck.Domain.Diagnostics;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>A CPU temperature reading: the median of several samples, and when it was taken.</summary>
public sealed record CpuTemperatureReading(double Celsius, DateTimeOffset TakenAtUtc);

/// <summary>
///     CPU temperature with LibreHardwareMonitorLib (MPL-2.0) and firmware throttling from the
///     event log (plan phase 6B, R-THERMAL). Since LibreHardwareMonitorLib 0.9.5 the CPU sensors
///     need the PawnIO driver installed on the PC; without it the temperature is "not
///     measurable". Zentra installs PawnIO only for a measurement, with consent, and removes it
///     afterwards (Diego's decision, 19/11/2026).
/// </summary>
public static class ThermalService
{
    /// <summary>The last reading taken on this PC while Zentra is open.</summary>
    public static CpuTemperatureReading? LastReading { get; private set; }

    /// <summary>A reading counts for the diagnosis for this long.</summary>
    public static readonly TimeSpan ReadingValidFor = TimeSpan.FromHours(2);

    /// <summary>Whether PawnIO is installed (the uninstall entry LibreHardwareMonitorLib itself checks).</summary>
    public static bool IsPawnIoInstalled()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"
            );
            if (key is not null)
                return true;
        }
        return false;
    }

    /// <summary>
    ///     Samples the CPU package temperature once a second and keeps the median. Null when
    ///     LibreHardwareMonitorLib finds no CPU temperature sensor (typically: PawnIO missing).
    /// </summary>
    public static async Task<CpuTemperatureReading?> MeasureAsync(
        TimeSpan duration,
        ILogger logger,
        CancellationToken ct
    )
    {
        var computer = new Computer { IsCpuEnabled = true };
        try
        {
            computer.Open();
            var samples = new List<double>();
            var end = DateTime.UtcNow + duration;
            do
            {
                if (ReadOnce(computer) is { } t)
                    samples.Add(t);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            } while (DateTime.UtcNow < end);

            if (samples.Count == 0)
            {
                logger.LogInformation(
                    "[THERMAL] no CPU temperature sensor (PawnIO installed: {Installed})",
                    IsPawnIoInstalled()
                );
                return null;
            }
            samples.Sort();
            var reading = new CpuTemperatureReading(
                Math.Round(samples[samples.Count / 2], 1),
                DateTimeOffset.UtcNow
            );
            LastReading = reading;
            logger.LogInformation(
                "[THERMAL] CPU {Celsius} °C (median of {Count} samples)",
                reading.Celsius,
                samples.Count
            );
            return reading;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[THERMAL] reading failed");
            return null;
        }
        finally
        {
            computer.Close();
        }
    }

    /// <summary>The hottest CPU package/die sensor (Tctl/Tdie on AMD, Package on Intel).</summary>
    private static double? ReadOnce(Computer computer)
    {
        double? max = null;
        foreach (var hardware in computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                if (
                    sensor.SensorType != SensorType.Temperature
                    || sensor.Value is not { } value
                    || value is <= 0 or > 150
                )
                    continue;
                var name = sensor.Name;
                if (
                    name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Core Max", StringComparison.OrdinalIgnoreCase)
                )
                    max = Math.Max(max ?? 0, value);
            }
        }
        return max;
    }

    /// <summary>
    ///     "The speed of processor N is being limited by system firmware" events
    ///     (Kernel-Processor-Power 37) in the last <paramref name="days" /> days.
    /// </summary>
    public static Measured<int> CountFirmwareThrottling(int days, DateTimeOffset now)
    {
        try
        {
            var since = now.UtcDateTime.AddDays(-days).ToString("o");
            var query = new EventLogQuery(
                "System",
                PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37) and TimeCreated[@SystemTime>='{since}']]]"
            );
            using var reader = new EventLogReader(query);
            var count = 0;
            while (reader.ReadEvent() is { } e)
            {
                e.Dispose();
                count++;
            }
            return Measured<int>.Of(count);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
        }
    }
}
