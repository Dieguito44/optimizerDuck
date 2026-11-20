using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.Diagnostics;

public sealed record SpeedTestResult(double? DownloadMbps, double? UploadMbps, double? LatencyMs);

/// <summary>
///     Plan of improvements F4-06: internet speed against Cloudflare's public speed test
///     (speed.cloudflare.com, the endpoints its own page uses; not a documented API, so a failure
///     is "not measurable"). Only after the technician confirms: Cloudflare sees the client's
///     public IP address. About 50 MB down and 10 MB up. Each figure is the median of its runs.
/// </summary>
public sealed class SpeedTestService(ILogger<SpeedTestService> logger)
{
    public const string Host = "speed.cloudflare.com";
    private const string Down = "https://speed.cloudflare.com/__down?bytes=";
    private const string Up = "https://speed.cloudflare.com/__up";

    private static readonly int[] DownloadSizes = [10_000_000, 10_000_000, 25_000_000];
    private static readonly int[] UploadSizes = [5_000_000, 5_000_000];
    private const int LatencyProbes = 8;

    public async Task<SpeedTestResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var client = HttpClientFactory.CreateClient(TimeSpan.FromSeconds(60), logger);

        progress?.Report("latency");
        var latency = await MedianAsync(
            Enumerable
                .Range(0, LatencyProbes)
                .Select(_ => (Func<Task<double?>>)(() => LatencyAsync(client, ct))),
            ct
        );

        progress?.Report("download");
        var download = await MedianAsync(
            DownloadSizes.Select(size =>
                (Func<Task<double?>>)(() => DownloadAsync(client, size, ct))
            ),
            ct
        );

        progress?.Report("upload");
        var upload = await MedianAsync(
            UploadSizes.Select(size => (Func<Task<double?>>)(() => UploadAsync(client, size, ct))),
            ct
        );

        var result = new SpeedTestResult(
            Round(download),
            Round(upload),
            latency is { } l ? Math.Round(l) : null
        );
        logger.LogInformation(
            "Speed test ({Host}): down {Down} Mbps, up {Up} Mbps, latency {Latency} ms",
            Host,
            result.DownloadMbps,
            result.UploadMbps,
            result.LatencyMs
        );
        return result;
    }

    private static double? Round(double? mbps) => mbps is { } v ? Math.Round(v, 1) : null;

    /// <summary>The median of the runs that worked; null when none did.</summary>
    internal static async Task<double?> MedianAsync(
        IEnumerable<Func<Task<double?>>> runs,
        CancellationToken ct
    )
    {
        var values = new List<double>();
        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();
            if (await run() is { } v)
                values.Add(v);
        }
        return Median(values);
    }

    internal static double? Median(List<double> values)
    {
        if (values.Count == 0)
            return null;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>Megabits per second for so many bytes in so long.</summary>
    internal static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000;

    private async Task<double?> LatencyAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                Down + "0",
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            response.EnsureSuccessStatusCode();
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogDebug(ex, "Latency probe failed");
            return null;
        }
    }

    private async Task<double?> DownloadAsync(HttpClient client, int bytes, CancellationToken ct)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                Down + bytes,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
                total += read;
            return total < bytes / 2 ? null : Mbps(total, watch.Elapsed);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or IOException or TaskCanceledException
                && !ct.IsCancellationRequested
            )
        {
            logger.LogDebug(ex, "Download run failed");
            return null;
        }
    }

    private async Task<double?> UploadAsync(HttpClient client, int bytes, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(new byte[bytes]);
            var watch = Stopwatch.StartNew();
            using var response = await client.PostAsync(Up, content, ct);
            response.EnsureSuccessStatusCode();
            return Mbps(bytes, watch.Elapsed);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogDebug(ex, "Upload run failed");
            return null;
        }
    }
}
