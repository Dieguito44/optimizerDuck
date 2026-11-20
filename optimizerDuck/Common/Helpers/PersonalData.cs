using System.IO;
using System.Text.RegularExpressions;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     Removes what could identify the person or the PC from a text: the API key, the paths with
///     the user's folder, the user, PC and domain names, e-mail and IP addresses, and any extra
///     term the caller knows (the BIOS serial, the client label). Used by the crash reports
///     (plan F4-05) and before anything goes to the AI (plan F1-07, Ley 1581: minimization).
/// </summary>
public static partial class PersonalData
{
    public const string NamePlaceholder = "<nombre>";
    public const string SerialPlaceholder = "<serial>";

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\\s""']+")]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4();

    /// <summary>The names of this PC and of the people on it: the process's and the client's.</summary>
    public static IReadOnlyList<string> LocalNames()
    {
        var names = new List<string?>
        {
            Environment.UserName,
            Environment.MachineName,
            Environment.UserDomainName,
        };
        try
        {
            // Elevated with another administrator, the client is the one signed in at the screen
            // (plan F2-05): C:\Users\<client>\AppData\Local.
            var local = Services.System.InteractiveUser.LocalAppDataFolder();
            names.Add(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(local))));
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The process's own names are still there.
        }
        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Redact(string text) => Redact(text, LocalNames(), []);

    /// <summary>
    ///     The text without personal data. <paramref name="names" /> become "&lt;nombre&gt;" and
    ///     <paramref name="serials" /> "&lt;serial&gt;"; terms shorter than 3 characters are kept,
    ///     so a two-letter name never eats every word that contains it.
    /// </summary>
    public static string Redact(
        string text,
        IEnumerable<string?> names,
        IEnumerable<string?> serials
    )
    {
        if (string.IsNullOrEmpty(text))
            return text;
        text = SecretRedactor.Redact(text);
        text = UserFolder().Replace(text, "$1<usuario>");
        text = ReplaceTerms(text, serials, SerialPlaceholder);
        text = ReplaceTerms(text, names, NamePlaceholder);
        text = Email().Replace(text, "<correo>");
        return IPv4().Replace(text, m => IsVersionLike(m.Value) ? m.Value : "<ip>");
    }

    private static string ReplaceTerms(string text, IEnumerable<string?> terms, string placeholder)
    {
        // Longest first, so "DESKTOP-JUAN" goes whole before "JUAN" is looked for.
        foreach (
            var term in terms
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 3)
                .Select(t => t!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(t => t.Length)
        )
            text = Regex.Replace(text, Regex.Escape(term), placeholder, RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>"10.0.26200.1" is Windows' version, not an address.</summary>
    private static bool IsVersionLike(string value) =>
        value.StartsWith("10.0.", StringComparison.Ordinal);
}
