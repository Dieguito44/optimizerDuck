using System.Diagnostics;
using System.IO;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     Opens web pages, PDFs, folders, files and Settings as the signed-in user, not as the
///     administrator Zentra runs as (plan of improvements F2-06, audit Z-30).
/// </summary>
/// <remarks>
///     <para>
///         <c>Process.Start</c> with <c>UseShellExecute</c> from an elevated process starts the
///         browser or the PDF viewer elevated too, and picks the program from the user's own
///         file associations (HKCU), which any program of that user can change: the same idea
///         as the known "fodhelper" elevation bypass.
///     </para>
///     <para>
///         Here the target goes to <c>explorer.exe</c> from the Windows folder instead. A new
///         explorer.exe hands the request to the desktop shell that is already running, as the
///         signed-in user, and exits; the page or file opens there, without elevation.
///         System tools that need administrator rights (Task Manager, Task Scheduler, Control
///         Panel) are not opened through here.
///     </para>
/// </remarks>
public static class UnelevatedShell
{
    /// <summary>Opens a URL (http, https, ms-settings), a file or a folder.</summary>
    /// <exception cref="ArgumentException">The target is empty or would break the command line.</exception>
    public static void Open(string target) => Start(Validate(target), select: false);

    /// <summary>Opens the folder of a file in Explorer with that file selected.</summary>
    public static void Select(string filePath) => Start(Validate(filePath), select: true);

    /// <summary>
    ///     Quotes, control characters and line breaks never belong in a URL or a path and would
    ///     let the target add its own arguments to explorer.exe.
    /// </summary>
    internal static string Validate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Nothing to open.", nameof(target));
        if (target.Any(c => c == '"' || char.IsControl(c)))
            throw new ArgumentException(
                "The target contains characters that cannot be opened.",
                nameof(target)
            );
        return target.Trim();
    }

    internal static string Arguments(string target, bool select) =>
        select ? $"/select,\"{target}\"" : $"\"{target}\"";

    private static void Start(string target, bool select)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            // Not elevated (tests, development): the normal shell call is already the user's.
            Process.Start(
                select
                    ? new ProcessStartInfo("explorer.exe", Arguments(target, select: true))
                    : new ProcessStartInfo(target) { UseShellExecute = true }
            );
            return;
        }

        Process.Start(
            new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe"
                ),
                Arguments = Arguments(target, select),
                UseShellExecute = false,
                WorkingDirectory = Environment.SystemDirectory,
            }
        );
    }
}
