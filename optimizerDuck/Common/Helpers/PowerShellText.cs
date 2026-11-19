using System.Text;

namespace optimizerDuck.Common.Helpers;

/// <summary>Text that goes inside PowerShell commands.</summary>
public static class PowerShellText
{
    /// <summary>
    ///     Every character PowerShell accepts as a single quote: the ASCII one and the four
    ///     typographic ones (‘ ’ ‚ ‛). Escaping only the ASCII quote lets a folder or app name
    ///     with a typographic quote close the literal and run its own command.
    /// </summary>
    private static readonly char[] SingleQuotes = ['\'', '‘', '’', '‚', '‛'];

    /// <summary>
    ///     Escapes a value for a single-quoted PowerShell literal ('...'), where variables and
    ///     subexpressions are never evaluated: each quote character is doubled.
    /// </summary>
    public static string EscapeSingleQuoted(string value)
    {
        var text = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            text.Append(c);
            if (Array.IndexOf(SingleQuotes, c) >= 0)
                text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>The value as a complete single-quoted PowerShell literal.</summary>
    public static string Quote(string value) => "'" + EscapeSingleQuoted(value) + "'";
}
