using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.Repair;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>Reads the phase 6B maintenance facts. Each read fails on its own.</summary>
public static partial class MaintenanceProbes
{
    private static readonly TimeSpan WingetTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     Counts the programs `winget upgrade` lists. The summary line is translated, so the
    ///     rows of the table are counted instead: after the dashed header line, every line with
    ///     column gaps (two or more spaces) up to the first line without them.
    /// </summary>
    public static async Task<Measured<int>> CountWingetUpgradesAsync(
        ILogger logger,
        CancellationToken ct
    )
    {
        try
        {
            var result = await LongConsoleRunner.RunAsync(
                "winget.exe",
                "upgrade --accept-source-agreements --disable-interactivity",
                WingetTimeout,
                null,
                logger,
                ct
            );
            if (result.TimedOut)
                return Measured<int>.Missing(NotMeasuredReason.TimedOut);
            return CountUpgradeRows(result.Output) is { } count
                ? Measured<int>.Of(count)
                : Measured<int>.Missing(NotMeasuredReason.QueryFailed);
        }
        catch (Exception ex)
            when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("winget is not available: {Error}", ex.Message);
            return Measured<int>.Missing(NotMeasuredReason.RequiresExternalTool);
        }
    }

    /// <summary>The number of upgrade rows, 0 when winget says there are none, null when unreadable.</summary>
    public static int? CountUpgradeRows(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var header = Array.FindIndex(lines, l => DashedLine().IsMatch(l));
        if (header < 0)
            // No table: winget prints a single line when nothing can be upgraded. Anything that
            // mentions an error is unreadable, not "zero".
            return
                lines.Length > 0
                && !lines.Any(l => l.Contains("0x8A", StringComparison.OrdinalIgnoreCase))
                ? 0
                : null;

        var count = 0;
        for (var i = header + 1; i < lines.Length && ColumnGap().Matches(lines[i]).Count >= 3; i++)
            count++;
        return count;
    }

    /// <summary>
    ///     Defender's PUA blocking. When Defender is not the active antivirus (passive or replaced),
    ///     the setting has no effect and the check does not apply.
    /// </summary>
    public static Measured<PuaMode> ReadPuaProtection()
    {
        const string Ns = @"root\Microsoft\Windows\Defender";
        var mode = WmiHelper.Query(
            "SELECT AMRunningMode FROM MSFT_MpComputerStatus",
            static items => items.Count > 0 ? WmiHelper.GetString(items[0], "AMRunningMode") : null,
            Ns
        );
        if (mode is null)
            return Measured<PuaMode>.Missing(NotMeasuredReason.QueryFailed);
        if (!mode.Equals("Normal", StringComparison.OrdinalIgnoreCase))
            return Measured<PuaMode>.Missing(NotMeasuredReason.NotApplicable);

        var value = WmiHelper.Query(
            "SELECT PUAProtection FROM MSFT_MpPreference",
            static items => items.Count > 0 ? WmiHelper.GetInt(items[0], "PUAProtection") : null,
            Ns
        );
        return value is >= 0 and <= 2
            ? Measured<PuaMode>.Of((PuaMode)value.Value)
            : Measured<PuaMode>.Missing(NotMeasuredReason.QueryFailed);
    }

    [GeneratedRegex(@"^-{10,}\s*$")]
    private static partial Regex DashedLine();

    [GeneratedRegex(@"\S {2,}(?=\S)")]
    private static partial Regex ColumnGap();
}
