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
            // A failed run (no network, broken source) is "not measurable", never "0 outdated"
            // (audit Z-18). A normal listing exits with 0, with or without upgrades.
            if (result.ExitCode != 0)
            {
                logger.LogWarning("winget upgrade failed with exit code {Code}", result.ExitCode);
                return Measured<int>.Missing(NotMeasuredReason.QueryFailed);
            }
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

    /// <summary>
    ///     Full charge capacity over design capacity (root\wmi). Not applicable on a PC without
    ///     a battery; not reported when the battery firmware gives no design capacity.
    /// </summary>
    public static Measured<int> ReadBatteryCapacityPercent()
    {
        const string Ns = @"root\wmi";
        var full = WmiHelper.Query(
            "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity",
            static items =>
                items.Count > 0 ? (long?)WmiHelper.GetLong(items[0], "FullChargedCapacity") : null,
            Ns
        );
        var design = WmiHelper.Query(
            "SELECT DesignedCapacity FROM BatteryStaticData",
            static items =>
                items.Count > 0 ? (long?)WmiHelper.GetLong(items[0], "DesignedCapacity") : null,
            Ns
        );
        if (full is null && design is null)
            return Measured<int>.Missing(NotMeasuredReason.NotApplicable);
        return full is > 0 && design is > 0
            ? Measured<int>.Of((int)Math.Round(100.0 * full.Value / design.Value))
            : Measured<int>.Missing(NotMeasuredReason.NotReported);
    }

    /// <summary>
    ///     The connected physical Ethernet adapter: its link speed, and the highest speed its
    ///     "Speed and Duplex" option offers (NDIS standard values: 4 = 100 Mb/s full, 5/6 = 1 Gb/s,
    ///     7 = 10 Gb/s). Not applicable without a wired connection.
    /// </summary>
    public static (Measured<int> Link, Measured<int> Max) ReadWiredLink()
    {
        const string Ns = @"root\StandardCimv2";
        var adapter = WmiHelper.Query(
            "SELECT Name, Speed, MediaConnectState, NdisPhysicalMedium, HardwareInterface FROM MSFT_NetAdapter",
            static items =>
                items
                    .Where(mo =>
                        WmiHelper.GetInt(mo, "MediaConnectState") == 1
                        && WmiHelper.GetInt(mo, "NdisPhysicalMedium") == 14 // 802.3
                        && mo["HardwareInterface"] is true
                    )
                    .Select(mo =>
                        (
                            Name: WmiHelper.GetString(mo, "Name"),
                            Speed: WmiHelper.GetLong(mo, "Speed")
                        )
                    )
                    .FirstOrDefault(),
            Ns
        );
        if (adapter.Name is null)
            return (
                Measured<int>.Missing(NotMeasuredReason.NotApplicable),
                Measured<int>.Missing(NotMeasuredReason.NotApplicable)
            );

        var link = adapter.Speed is > 0
            ? Measured<int>.Of((int)(adapter.Speed.Value / 1_000_000))
            : Measured<int>.Missing(NotMeasuredReason.NotReported);
        var values = WmiHelper.Query(
            $"SELECT ValidRegistryValues FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name = '{adapter.Name.Replace("'", "''")}' AND RegistryKeyword = '*SpeedDuplex'",
            static items => items.Count > 0 ? items[0]["ValidRegistryValues"] as string[] : null,
            Ns
        );
        int? max = values
            ?.Select(v =>
                v switch
                {
                    "1" or "2" => 10,
                    "3" or "4" => 100,
                    "5" or "6" => 1000,
                    "7" => 10000,
                    _ => 0,
                }
            )
            .DefaultIfEmpty(0)
            .Max();
        return (
            link,
            max is > 0
                ? Measured<int>.Of(max.Value)
                : Measured<int>.Missing(NotMeasuredReason.NotReported)
        );
    }

    public static Measured<int> CountRestorePoints()
    {
        // Without elevation the query fails and the service returns an empty list: that would look
        // like "no restore points".
        if (!Environment.IsPrivilegedProcess)
            return Measured<int>.Missing(NotMeasuredReason.AccessDenied);
        try
        {
            var points = new SystemRestoreService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemRestoreService>.Instance
            ).ListRestorePoints();
            return Measured<int>.Of(points.Count);
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or global::System.Management.ManagementException
            )
        {
            return Measured<int>.Missing(NotMeasuredReason.AccessDenied);
        }
    }

    /// <summary>A OneDrive account with a local folder (HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder).</summary>
    public static Measured<bool> ReadOneDriveConfigured()
    {
        using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\OneDrive\Accounts"
        );
        if (accounts is null)
            return Measured<bool>.Of(false);
        foreach (var name in accounts.GetSubKeyNames())
        {
            using var account = accounts.OpenSubKey(name);
            if (account?.GetValue("UserFolder") is string folder && folder.Length > 0)
                return Measured<bool>.Of(true);
        }
        return Measured<bool>.Of(false);
    }
}
