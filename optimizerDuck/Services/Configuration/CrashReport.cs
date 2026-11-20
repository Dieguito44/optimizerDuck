using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Services.Configuration;

/// <summary>
///     Plan of improvements F4-05: crash reports, off by default. When the technician turns them
///     on (Settings), each crash also leaves a report without personal data: no user or PC name,
///     no paths with the user's folder, no e-mail, no IP address, no API key. Zentra never sends
///     it: the technician reads it, copies it and sends it to whoever maintains Zentra. That keeps
///     it out of a new automatic data transfer (Ley 1581).
/// </summary>
public static partial class CrashReport
{
    /// <summary>Set at startup from the settings, and when the switch changes.</summary>
    public static bool Enabled { get; set; }

    public static string Directory => Path.Combine(Shared.RootDirectory, "Crashes", "Reportes");

    /// <summary>Writes a report when enabled. Never throws: it runs while the app is failing.</summary>
    public static string? TryWrite(string category, Exception ex)
    {
        if (!Enabled)
            return null;
        try
        {
            global::System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(
                Directory,
                $"reporte_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.txt"
            );
            File.WriteAllText(path, Build(category, ex), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static string? Latest()
    {
        try
        {
            return global::System.IO.Directory.Exists(Directory)
                ? global::System
                    .IO.Directory.EnumerateFiles(Directory, "reporte_*.txt")
                    .OrderDescending()
                    .FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What the report says: version, Windows, the exception and its stack, redacted.</summary>
    public static string Build(string category, Exception ex) =>
        Redact(
            $"""
            Zentra {Shared.FileVersion} · {RuntimeInformation.OSDescription} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}
            [{category}] {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
            {ex.GetType().FullName}: {ex.Message}
            {ex.StackTrace}
            {(
                ex.InnerException is { } inner
                    ? $"Inner: {inner.GetType().FullName}: {inner.Message}{Environment.NewLine}{inner.StackTrace}"
                    : ""
            )}
            """
        );

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\\s""']+")]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4();

    /// <summary>Removes what could identify the person or the PC. Public so it can be tested.</summary>
    public static string Redact(string text)
    {
        text = SecretRedactor.Redact(text);
        text = UserFolder().Replace(text, "$1<usuario>");
        foreach (
            var name in new[]
            {
                Environment.UserName,
                Environment.MachineName,
                Environment.UserDomainName,
            }
        )
            if (!string.IsNullOrWhiteSpace(name) && name.Length >= 3)
                text = Regex.Replace(text, Regex.Escape(name), "<nombre>", RegexOptions.IgnoreCase);
        text = Email().Replace(text, "<correo>");
        return IPv4().Replace(text, m => IsVersionLike(m.Value) ? m.Value : "<ip>");
    }

    /// <summary>"10.0.26200.1" is Windows' version, not an address: kept when it follows "Windows"/"Zentra".</summary>
    private static bool IsVersionLike(string value) =>
        value.StartsWith("10.0.", StringComparison.Ordinal);
}
