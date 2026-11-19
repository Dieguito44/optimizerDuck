using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Diagnostics;

namespace optimizerDuck.Services.Measurements;

/// <summary>Result of the read-only disk benchmark.</summary>
public sealed record DiskBenchmarkResult(
    Measured<double> SequentialReadMiBps,
    Measured<double> RandomReadIops
);

/// <summary>
///     Read-only disk benchmark with DiskSpd (built by Zentra from Microsoft's MIT source).
///     It creates one 1 GiB test file on the system drive, reads it sequentially and at random
///     for 15 s each with caching off, and deletes it. Nothing is written after the file is
///     created, so a worn client SSD is not stressed.
/// </summary>
public sealed class DiskBenchmark(ILogger<DiskBenchmark> logger)
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Where Zentra ships diskspd.exe, next to the app.</summary>
    public static string ToolPath => Path.Combine(AppContext.BaseDirectory, "Tools", "diskspd.exe");

    private static string TestFile =>
        Path.Combine(Shared.RootDirectory, "Bench", "zentra-bench.dat");

    public async Task<DiskBenchmarkResult> RunAsync(CancellationToken ct)
    {
        if (!File.Exists(ToolPath))
        {
            logger.LogWarning("DiskSpd not found at {Path}", ToolPath);
            return Missing(NotMeasuredReason.RequiresExternalTool);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TestFile)!);
        try
        {
            // -c1G creates the file once; the second run reuses it. -w0 = reads only.
            // -Sh = no software cache and no hardware write cache. -W2 = 2 s warm-up.
            var sequential = await RunOnceAsync(
                    $"-c1G -b1M -d15 -o8 -t1 -w0 -Sh -W2 \"{TestFile}\"",
                    ct
                )
                .ConfigureAwait(false);
            var random = await RunOnceAsync($"-b4K -d15 -o32 -t4 -r -w0 -Sh -W2 \"{TestFile}\"", ct)
                .ConfigureAwait(false);

            return new DiskBenchmarkResult(
                sequential is { } s
                    ? Measured<double>.Of(Math.Round(s.MiBps, 1))
                    : Measured<double>.Missing(NotMeasuredReason.QueryFailed),
                random is { } r
                    ? Measured<double>.Of(Math.Round(r.Iops))
                    : Measured<double>.Missing(NotMeasuredReason.QueryFailed)
            );
        }
        finally
        {
            try
            {
                File.Delete(TestFile);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete the benchmark file {File}", TestFile);
            }
        }
    }

    private async Task<DiskSpdTotals?> RunOnceAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ToolPath, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(RunTimeout, ct).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                logger.LogWarning("DiskSpd exited with {Code}", process.ExitCode);
                return null;
            }
            return ParseTotals(text);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            TryKill(process);
            if (ex is OperationCanceledException)
                throw;
            logger.LogWarning("DiskSpd timed out");
            return null;
        }
        catch (Win32Exception ex)
        {
            // Includes the case where Smart App Control or antivirus blocks the tool.
            logger.LogWarning(ex, "DiskSpd could not start");
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    ///     Reads the first "total:" row of DiskSpd's text output (the Total IO table):
    ///     <c>total: bytes | I/Os | MiB/s | I/O per s | ...</c>.
    /// </summary>
    public static DiskSpdTotals? ParseTotals(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("total:", StringComparison.OrdinalIgnoreCase))
                continue;

            var columns = line["total:".Length..].Split('|').Select(c => c.Trim()).ToArray();
            if (
                columns.Length >= 4
                && double.TryParse(
                    columns[2],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var mibps
                )
                && double.TryParse(
                    columns[3],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var iops
                )
            )
                return new DiskSpdTotals(mibps, iops);
            return null;
        }
        return null;
    }

    private static DiskBenchmarkResult Missing(NotMeasuredReason reason) =>
        new(Measured<double>.Missing(reason), Measured<double>.Missing(reason));
}

/// <summary>Totals of one DiskSpd run.</summary>
public sealed record DiskSpdTotals(double MiBps, double Iops);
