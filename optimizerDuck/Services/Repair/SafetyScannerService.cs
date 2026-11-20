using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.Repair;

public enum SafetyScanOutcome
{
    /// <summary>"No infection found."</summary>
    Clean,

    /// <summary>The scanner listed threats (detect only: nothing was removed).</summary>
    ThreatsFound,

    /// <summary>The scan ran but its log could not be read: "not measurable", never "clean".</summary>
    Unreadable,

    /// <summary>Download, signature or run failed.</summary>
    Failed,
}

public sealed record SafetyScanResult(
    SafetyScanOutcome Outcome,
    IReadOnlyList<string> Threats,
    string? LogCopyPath
);

/// <summary>
///     Plan of improvements F3-08: a second opinion with the Microsoft Safety Scanner (MSERT),
///     next to the Defender scans. It downloads MSERT from Microsoft, checks that Microsoft
///     signed it (Authenticode, chain to a Microsoft root) with the file locked against writes,
///     runs a quick scan in detect-only mode (/Q /N: nothing is removed, the technician decides
///     with Defender), copies that run's part of %WINDIR%\debug\msert.log to Zentra's folder and
///     deletes the 200 MB scanner. MSERT expires 10 days after download, so it is never kept.
/// </summary>
public sealed class SafetyScannerService(ILogger<SafetyScannerService> logger)
{
    /// <summary>Permanent record key of the Safety Scanner tool. Never change it.</summary>
    public static readonly OperationSubject Subject = new(
        Guid.Parse("8F3B6D21-4A7C-4E95-B0D8-6C1E2A9F7B43"),
        "SafetyScanner",
        "Microsoft Safety Scanner"
    );

    /// <summary>Microsoft's download links (learn.microsoft.com, "Microsoft Safety Scanner").</summary>
    public const string Download64 = "https://go.microsoft.com/fwlink/?LinkId=212732";

    public const string Download32 = "https://go.microsoft.com/fwlink/?LinkId=212733";

    /// <summary>Quiet, detect only. A quick scan is the default.</summary>
    public const string Arguments = "/Q /N";

    private const long MaxBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromHours(2);

    public static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "debug",
            "msert.log"
        );

    public static string LogCopyDirectory => Path.Combine(Shared.RootDirectory, "SafetyScanner");

    public OperationRequest BuildRequest(Action<SafetyScanResult> onResult) =>
        new(
            Subject,
            Loc.Instance["SafetyScanner.Title"],
            logger,
            RevertPersistence.Disabled,
            async (progress, context) =>
            {
                var result = await RunAsync(progress, context.CancellationToken);
                onResult(result);
                var command = $"msert.exe {Arguments}";
                if (result.Outcome is SafetyScanOutcome.Failed)
                    context.Changes.Add(
                        ServiceStrings.ShellName,
                        command,
                        false,
                        error: Loc.Instance["SafetyScanner.Failed"]
                    );
                else
                    context.Changes.AddIrreversible(ServiceStrings.ShellName, command);
                return context.Changes.ToApplyResult();
            }
        );

    private async Task<SafetyScanResult> RunAsync(
        IProgress<ProcessingProgress> progress,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(Shared.DownloadsDirectory);
        var exe = Path.Combine(Shared.DownloadsDirectory, $"msert-{Guid.NewGuid():N}.exe");
        try
        {
            if (!await DownloadAsync(exe, progress, ct))
                return new(SafetyScanOutcome.Failed, [], null);

            // Locked against writes from the signature check to the end of the scan, so the file
            // that was checked is the file that runs.
            using var locked = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!IsSignedByMicrosoft(exe, locked.SafeFileHandle))
            {
                logger.LogWarning("Safety Scanner refused: not signed by Microsoft");
                return new(SafetyScanOutcome.Failed, [], null);
            }

            var runsBefore = CountRuns(ReadLog());
            progress.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance["SafetyScanner.Scanning"],
                    IsIndeterminate = true,
                }
            );
            var run = await LongConsoleRunner.RunAsync(
                exe,
                Arguments,
                ScanTimeout,
                null,
                logger,
                ct
            );
            logger.LogInformation(
                "Safety Scanner: exit {Code}{TimedOut}",
                run.ExitCode,
                run.TimedOut ? " (timed out)" : ""
            );
            if (run.TimedOut)
                return new(SafetyScanOutcome.Failed, [], null);

            var section = LastRun(ReadLog(), runsBefore);
            if (section is null)
                return new(SafetyScanOutcome.Unreadable, [], null);

            Directory.CreateDirectory(LogCopyDirectory);
            var copy = Path.Combine(LogCopyDirectory, $"msert-{DateTime.Now:yyyy-MM-dd_HHmm}.log");
            await File.WriteAllTextAsync(copy, section, Encoding.UTF8, ct);
            var (outcome, threats) = Parse(section);
            return new(outcome, threats, copy);
        }
        catch (Exception ex)
            when (ex
                    is HttpRequestException
                        or IOException
                        or UnauthorizedAccessException
                        or global::System.ComponentModel.Win32Exception
            )
        {
            logger.LogWarning(ex, "Safety Scanner failed");
            return new(SafetyScanOutcome.Failed, [], null);
        }
        finally
        {
            try
            {
                File.Delete(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {File}", exe);
            }
        }
    }

    /// <summary>Streams the download with progress. Only microsoft.com may serve it.</summary>
    private async Task<bool> DownloadAsync(
        string path,
        IProgress<ProcessingProgress> progress,
        CancellationToken ct
    )
    {
        var url = Environment.Is64BitOperatingSystem ? Download64 : Download32;
        using var client = HttpClientFactory.CreateClient(TimeSpan.FromMinutes(30), logger);
        using var response = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();
        var finalHost = response.RequestMessage?.RequestUri?.Host ?? "";
        if (!IsMicrosoftHost(finalHost))
        {
            logger.LogWarning("Safety Scanner download refused: served by {Host}", finalHost);
            return false;
        }

        var total = response.Content.Headers.ContentLength;
        if (total > MaxBytes)
            return false;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None
        );
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            done += read;
            if (done > MaxBytes)
                return false;
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            progress.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance["SafetyScanner.Downloading", done / (1024 * 1024)],
                    Value = total is > 0 ? (int)(done * 100 / total.Value) : 0,
                    Total = 100,
                    IsIndeterminate = total is not > 0,
                }
            );
        }
        logger.LogInformation(
            "Safety Scanner downloaded: {Bytes} bytes from {Host}",
            done,
            finalHost
        );
        return true;
    }

    internal static bool IsMicrosoftHost(string host) =>
        host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase);

    // ---- Signature ---------------------------------------------------------------------------

    /// <summary>
    ///     Authenticode valid (WinVerifyTrust, through the locked handle), signer organization
    ///     "Microsoft Corporation" and a chain that ends in a Microsoft root.
    /// </summary>
    internal static bool IsSignedByMicrosoft(string path, SafeFileHandle? handle = null)
    {
        if (WinTrust.Verify(path, handle) != 0)
            return false;
        try
        {
#pragma warning disable SYSLIB0057 // The signer of a signed file; no non-obsolete equivalent.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!signer.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal))
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // WinVerifyTrust checked it
            chain.Build(signer);
            var root = chain.ChainElements[^1].Certificate;
            return root.Subject.Contains("CN=Microsoft Root", StringComparison.Ordinal);
        }
        catch (global::System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint Size;
            public string FilePath;
            public IntPtr File;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice; // 2 = none
            public uint RevocationChecks; // 1 = whole chain
            public uint UnionChoice; // 1 = file
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProvFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr window,
            ref Guid action,
            ref TrustData data
        );

        /// <summary>0 when the embedded signature is valid and trusted.</summary>
        public static int Verify(string path, SafeFileHandle? handle)
        {
            var file = new FileInfo
            {
                Size = (uint)Marshal.SizeOf<FileInfo>(),
                FilePath = path,
                File = handle?.DangerousGetHandle() ?? IntPtr.Zero,
            };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new TrustData
                {
                    Size = (uint)Marshal.SizeOf<TrustData>(),
                    UiChoice = 2,
                    RevocationChecks = 1,
                    UnionChoice = 1,
                    FileInfo = filePtr,
                    ProvFlags = 0x80, // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                };
                var action = GenericVerifyV2;
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(filePtr);
                Marshal.FreeHGlobal(filePtr);
            }
        }
    }

    // ---- The log -----------------------------------------------------------------------------

    private static string ReadLog()
    {
        try
        {
            // msert.log is UTF-16 with a BOM; StreamReader picks the encoding from it.
            return File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private const string RunHeader = "Microsoft Safety Scanner v";

    /// <summary>The last run in the log, when the log has more runs than before the scan.</summary>
    internal static string? LastRun(string log, int runsBefore)
    {
        if (CountRuns(log) <= runsBefore)
            return null;
        return log[log.LastIndexOf(RunHeader, StringComparison.Ordinal)..].Trim();
    }

    internal static int CountRuns(string text)
    {
        var count = 0;
        for (
            var i = text.IndexOf(RunHeader, StringComparison.Ordinal);
            i >= 0;
            i = text.IndexOf(RunHeader, i + 1, StringComparison.Ordinal)
        )
            count++;
        return count;
    }

    /// <summary>
    ///     The run's result: "No infection found." is clean; "Found X..." or "Threat Detected: X"
    ///     lines are threats; anything else is unreadable, never clean.
    /// </summary>
    internal static (SafetyScanOutcome Outcome, IReadOnlyList<string> Threats) Parse(string run)
    {
        var threats = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in run.Split('\n'))
        {
            var line = raw.Trim();
            var name =
                line.StartsWith("Threat Detected:", StringComparison.Ordinal) ? line[16..]
                : line.StartsWith("Found ", StringComparison.Ordinal) ? line[6..]
                : null;
            if (name is null)
                continue;
            name = name.Split([',', ' '], 2, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(name))
                threats.Add(name.Trim());
        }
        if (threats.Count > 0)
            return (SafetyScanOutcome.ThreatsFound, threats.ToList());
        return run.Contains("No infection found", StringComparison.OrdinalIgnoreCase)
            ? (SafetyScanOutcome.Clean, [])
            : (SafetyScanOutcome.Unreadable, []);
    }
}
