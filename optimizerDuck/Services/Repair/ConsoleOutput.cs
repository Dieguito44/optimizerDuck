using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace optimizerDuck.Services.Repair;

/// <summary>
///     Decodes what console tools write when redirected. Each tool does it differently: sfc
///     writes UTF-16LE, DISM and chkdsk write in the console's OEM code page (850 in Spanish).
///     Reading everything as UTF-8 is what produced the broken characters of the old toolkit.
/// </summary>
public static partial class ConsoleOutput
{
    static ConsoleOutput()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Decode(ReadOnlySpan<byte> bytes) =>
        Decode(bytes, CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    public static string Decode(ReadOnlySpan<byte> bytes, int oemCodePage)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        // UTF-16LE: BOM, or many zero bytes in odd positions (ASCII text stored as 2 bytes).
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (LooksUtf16Le(bytes))
            return Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(oemCodePage).GetString(bytes);
        }
    }

    private static bool LooksUtf16Le(ReadOnlySpan<byte> bytes)
    {
        var pairs = Math.Min(bytes.Length / 2, 512);
        if (pairs < 4)
            return false;
        var zerosInOdd = 0;
        for (var i = 0; i < pairs; i++)
            if (bytes[i * 2 + 1] == 0)
                zerosInOdd++;
        return zerosInOdd * 10 >= pairs * 6; // 60 % or more
    }

    /// <summary>The last percentage in the text ("[=== 45.3% ===]", "Verificación 45% completada").</summary>
    public static int? LastPercent(string text)
    {
        var matches = PercentRegex().Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var raw = matches[i].Groups[1].Value.Replace(',', '.');
            if (
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && v is >= 0 and <= 100
            )
                return (int)Math.Floor(v);
        }
        return null;
    }

    /// <summary>The last non-empty lines, cleaned of progress bars, for a short summary.</summary>
    public static string Tail(string text, int lines = 4)
    {
        var clean = text.Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('[') && !BarRegex().IsMatch(l))
            .ToList();
        return string.Join(Environment.NewLine, clean.TakeLast(lines));
    }

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s?%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"^[=\s\d.,%]+$")]
    private static partial Regex BarRegex();
}

/// <summary>Result of one long console tool run.</summary>
public sealed record ConsoleRunResult(int ExitCode, string Output, bool TimedOut);

/// <summary>
///     Runs a long console tool (DISM, sfc, chkdsk) with a time limit fitting it, reading its
///     raw output to report progress as it goes. Kills the tool on cancel or timeout.
/// </summary>
public static class LongConsoleRunner
{
    public static async Task<ConsoleRunResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        Action<int?, string>? onProgress,
        ILogger? logger,
        CancellationToken ct
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var buffer = new MemoryStream();
        var gate = new Lock();
        var reader = Task.Run(
            async () =>
            {
                var chunk = new byte[4096];
                var stream = process.StandardOutput.BaseStream;
                int read;
                while ((read = await stream.ReadAsync(chunk, CancellationToken.None)) > 0)
                    lock (gate)
                        buffer.Write(chunk, 0, read);
            },
            CancellationToken.None
        );
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        try
        {
            while (!process.HasExited)
            {
                await Task.Delay(500, linked.Token).ConfigureAwait(false);
                if (onProgress is not null)
                {
                    string text;
                    lock (gate)
                        text = ConsoleOutput.Decode(
                            buffer.GetBuffer().AsSpan(0, (int)buffer.Length)
                        );
                    onProgress(ConsoleOutput.LastPercent(text), ConsoleOutput.Tail(text, 1));
                }
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            logger?.LogWarning("{Tool} {Reason}", fileName, timedOut ? "timed out" : "cancelled");
            if (!timedOut)
                throw;
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await reader.ConfigureAwait(false);

        string output;
        lock (gate)
            output = ConsoleOutput.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        return new ConsoleRunResult(timedOut ? -1 : process.ExitCode, output, timedOut);
    }
}
