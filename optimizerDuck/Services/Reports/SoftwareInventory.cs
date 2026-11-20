using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Repair;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Reports;

/// <summary>Windows edition, version and activation, for the inventory. Never a product key.</summary>
public sealed record WindowsLicense(
    string? EditionId,
    string? DisplayVersion,
    int? Build,
    Measured<bool> Activated
);

/// <summary>
///     Plan of improvements F3-06: the list of installed programs and the Windows activation state,
///     to migrate to a new PC or for the client's own records. Read-only: nothing is activated,
///     installed or removed, and no product key is read. Local only, never sent to the AI.
/// </summary>
public static class SoftwareInventory
{
    public const string CsvName = "inventario.csv";
    public const string WingetName = "winget-export.json";

    private static readonly TimeSpan WingetTimeout = TimeSpan.FromSeconds(120);

    public static WindowsLicense ReadWindows()
    {
        var windows = WindowsProvider.Get();
        return new WindowsLicense(
            windows.EditionId,
            windows.DisplayVersion,
            windows.BuildNumber,
            DiagnosticProbes.ReadActivation()
        );
    }

    /// <summary>
    ///     Writes inventario.csv (and winget-export.json when winget can produce it) into the folder.
    ///     Returns the files written.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WriteAsync(
        string folder,
        ILogger logger,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(folder);
        var csv = Path.Combine(folder, CsvName);
        var entries = await Task.Run(InstalledAppsReader.ReadAll, ct);
        var windows = await Task.Run(ReadWindows, ct);
        // UTF-8 with BOM, so Excel opens the accents right.
        await File.WriteAllTextAsync(
            csv,
            ToCsv(entries, windows, Loc.CurrentCulture),
            new UTF8Encoding(true),
            ct
        );
        logger.LogInformation("Inventory written: {Count} programs", entries.Count);

        var files = new List<string> { csv };
        if (await WingetExportAsync(Path.Combine(folder, WingetName), logger, ct) is { } winget)
            files.Add(winget);
        return files;
    }

    /// <summary>
    ///     The CSV: a Windows line, then one line per program. Separated with the culture's list
    ///     separator (";" in Spanish), so Excel splits the columns by itself.
    /// </summary>
    public static string ToCsv(
        IReadOnlyList<InventoryEntry> entries,
        WindowsLicense windows,
        CultureInfo culture
    )
    {
        var sep = culture.TextInfo.ListSeparator;
        var text = new StringBuilder();
        void Line(params string?[] cells) =>
            text.AppendLine(string.Join(sep, cells.Select(c => Cell(c, sep))));

        Line(
            Loc.Instance["Inventory.Col.Name"],
            Loc.Instance["Inventory.Col.Version"],
            Loc.Instance["Inventory.Col.Publisher"],
            Loc.Instance["Inventory.Col.Installed"],
            Loc.Instance["Inventory.Col.SizeMb"],
            Loc.Instance["Inventory.Col.Scope"]
        );
        Line(
            "Windows " + (windows.EditionId ?? ""),
            string.Join(
                " ",
                new[] { windows.DisplayVersion, windows.Build?.ToString(culture) }.Where(v =>
                    v is not null
                )
            ),
            "Microsoft",
            null,
            null,
            windows.Activated.TryGet(out var activated)
                ? Loc.Instance[
                    activated ? "Inventory.Windows.Activated" : "Inventory.Windows.NotActivated"
                ]
                : Loc.Instance["Inventory.Windows.Unknown"]
        );
        foreach (var e in entries)
            Line(
                e.Name,
                e.Version,
                e.Publisher,
                e.InstalledOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                e.SizeKb is { } kb ? (kb / 1024.0).ToString("0.0", culture) : null,
                Loc.Instance[$"Inventory.Scope.{e.Scope}"]
            );
        return text.ToString();
    }

    /// <summary>
    ///     Quotes a cell when it needs it, and defuses formulas: a program name that starts with
    ///     =, +, - or @ would run as a formula when the CSV is opened in Excel.
    /// </summary>
    internal static string Cell(string? value, string separator)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.Contains(separator) || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>
    ///     "winget export": the programs winget knows, as a file "winget import" can reinstall on the
    ///     new PC. Null when winget is missing or fails; the CSV is the complete list anyway.
    /// </summary>
    private static async Task<string?> WingetExportAsync(
        string path,
        ILogger logger,
        CancellationToken ct
    )
    {
        try
        {
            var result = await LongConsoleRunner.RunAsync(
                "winget.exe",
                $"export -o \"{path}\" --include-versions --accept-source-agreements --disable-interactivity",
                WingetTimeout,
                null,
                logger,
                ct
            );
            // winget exits non-zero when some programs are not in any source, but still writes
            // the ones it knows: the file is what counts.
            if (!result.TimedOut && File.Exists(path) && new FileInfo(path).Length > 0)
                return path;
            logger.LogWarning(
                "winget export wrote nothing (exit code {Code}, timed out {TimedOut})",
                result.ExitCode,
                result.TimedOut
            );
            return null;
        }
        catch (Exception ex)
            when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("winget is not available: {Error}", ex.Message);
            return null;
        }
    }
}
