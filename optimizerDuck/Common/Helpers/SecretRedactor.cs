using System.Text.RegularExpressions;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     Safety net for logs and crash files: replaces anything that looks like an Anthropic API key
///     with a placeholder. The key store never logs the key; this catches the cases nobody planned
///     for (an exception message that carries a request, a header dump).
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "[clave oculta]";

    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? string.Empty : KeyPattern().Replace(text, Placeholder);

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex KeyPattern();
}
