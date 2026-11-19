using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>
///     Plan of improvements F2-09: total host writes of each SSD through smartctl (smartmontools
///     7.5, GPL-2.0, shipped unmodified next to the app; see Tools\smartctl.SOURCE.txt). Windows
///     does not decode SATA vendor attributes, so without it SATA wear is "not measurable".
///     Read-only: "smartctl -j -x" only reads SMART data and logs, it never starts a self-test
///     or changes a setting. Zentra runs the file only when its SHA-256 is the expected one.
/// </summary>
internal static class SmartctlProbe
{
    /// <summary>SHA-256 of the smartctl.exe Zentra ships (x64, 7.5 r5714).</summary>
    internal const string ExpectedSha256 =
        "B5DB94E5082C042BE44994B7A4FA8F7B5C8E713B2AB1C9A560D8F7A7995EA27D";

    private const int PerDiskTimeoutMs = 15_000;
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    /// <summary>Anything above one exabyte is a misread counter, not real writes.</summary>
    private const long Implausible = 1_000_000_000_000_000_000L;

    public static string ToolPath =>
        Path.Combine(AppContext.BaseDirectory, "Tools", "smartctl.exe");

    /// <summary>SSDs and NVMe drives that are not behind USB (bridges hide or garble SMART).</summary>
    internal static bool IsCandidate(DiskProbeRow row) =>
        row.BusType != 7
        && DiagnosticSnapshotBuilder.MapMediaType(row.MediaType, row.BusType)
            is StorageMediaType.Ssd
                or StorageMediaType.Nvme;

    /// <summary>The same rows, with host writes (or the reason they are missing) on each SSD.</summary>
    public static IReadOnlyList<DiskProbeRow> WithHostWrites(IReadOnlyList<DiskProbeRow> rows)
    {
        if (!rows.Any(IsCandidate))
            return rows;

        if (!Environment.IsPrivilegedProcess)
            return Mark(rows, NotMeasuredReason.AccessDenied);

        FileStream? tool;
        try
        {
            // Held open without write sharing while smartctl runs, so the checked file is the
            // file that runs.
            tool = new FileStream(ToolPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Mark(rows, NotMeasuredReason.RequiresExternalTool);
        }

        using (tool)
        {
            if (!HasExpectedHash(tool))
                return Mark(rows, NotMeasuredReason.QueryFailed);

            return rows.Select(r => IsCandidate(r) ? Read(r) : r).ToList();
        }
    }

    internal static bool HasExpectedHash(Stream file) =>
        string.Equals(
            Convert.ToHexString(SHA256.HashData(file)),
            ExpectedSha256,
            StringComparison.OrdinalIgnoreCase
        );

    private static IReadOnlyList<DiskProbeRow> Mark(
        IReadOnlyList<DiskProbeRow> rows,
        NotMeasuredReason reason
    ) => rows.Select(r => IsCandidate(r) ? r with { HostWritesReason = reason } : r).ToList();

    private static DiskProbeRow Read(DiskProbeRow row)
    {
        var (json, reason) = Run($"-j -x /dev/pd{row.Number}");
        if (json is null)
            return row with { HostWritesReason = reason };

        try
        {
            return ParseHostWrites(json) is { } bytes
                ? row with
                {
                    HostWritesBytes = bytes,
                }
                : row with
                {
                    HostWritesReason = NotMeasuredReason.NotReported,
                };
        }
        catch (JsonException)
        {
            return row with { HostWritesReason = NotMeasuredReason.QueryFailed };
        }
    }

    private static (string? Json, NotMeasuredReason Reason) Run(string arguments)
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = ToolPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(ToolPath)!,
                }
            );
            if (process is null)
                return (null, NotMeasuredReason.QueryFailed);

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(PerDiskTimeoutMs))
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException) { }
                return (null, NotMeasuredReason.TimedOut);
            }

            // Exit status is a bit mask: bit 0 = bad command line, bit 1 = the device could not
            // be opened. The other bits describe the drive, and the JSON is still complete.
            if ((process.ExitCode & 0b11) != 0)
                return (null, NotMeasuredReason.QueryFailed);
            return (output.GetAwaiter().GetResult(), NotMeasuredReason.NotReported);
        }
        catch (Exception ex)
            when (ex
                    is global::System.ComponentModel.Win32Exception
                        or IOException
                        or InvalidOperationException
            )
        {
            return (null, NotMeasuredReason.QueryFailed);
        }
    }

    /// <summary>
    ///     Total host writes in bytes from "smartctl -j" output, or null when the drive does not
    ///     report them in a unit that is known. In order of trust:
    ///     1. NVMe health log "Data Units Written" (1 unit = 1000 sectors of 512 bytes, by spec).
    ///     2. ATA Device Statistics "Logical Sectors Written" (ACS standard, General Statistics).
    ///     3. A vendor attribute whose name in smartctl's drive database states its unit
    ///        (Lifetime_Writes_GiB, Host_Writes_32MiB, ...). Attributes counted in sectors are used
    ///        only when the model is in that database: the default name alone does not prove the unit.
    ///     NAND or flash writes are never used: they include write amplification, TBW does not.
    /// </summary>
    internal static long? ParseHostWrites(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (
            root.TryGetProperty("nvme_smart_health_information_log", out var nvme)
            && TryLong(nvme, "data_units_written", out var units)
        )
            return Plausible(units, 512_000);

        var sector =
            root.TryGetProperty("logical_block_size", out var lbs)
            && lbs.TryGetInt32(out var size)
            && size > 0
                ? size
                : 512;

        if (DeviceStatisticsSectorsWritten(root) is { } sectors)
            return Plausible(sectors, sector);

        if (
            !root.TryGetProperty("ata_smart_attributes", out var attributes)
            || !attributes.TryGetProperty("table", out var table)
            || table.ValueKind != JsonValueKind.Array
        )
            return null;

        var inDatabase =
            root.TryGetProperty("in_smartctl_database", out var known)
            && known.ValueKind == JsonValueKind.True;

        foreach (var attribute in table.EnumerateArray())
        {
            if (
                !attribute.TryGetProperty("name", out var nameElement)
                || !attribute.TryGetProperty("raw", out var raw)
                || !TryLong(raw, "value", out var value)
            )
                continue;

            long? unit = nameElement.GetString() switch
            {
                "Lifetime_Writes_GiB" or "Host_Writes_GiB" or "Total_Writes_GiB" => GiB,
                "Host_Writes_32MiB" => 32 * MiB,
                "Host_Writes_MiB" => MiB,
                "Total_Writes_GB" => 1_000_000_000L,
                "Total_LBAs_Written"
                or "Total_Host_LBA_Written"
                or "Write_Sectors_Tot_Ct" when inDatabase => sector,
                _ => null,
            };
            if (unit is { } multiplier)
                return Plausible(value, multiplier);
        }

        return null;
    }

    private static long? DeviceStatisticsSectorsWritten(JsonElement root)
    {
        if (
            !root.TryGetProperty("ata_device_statistics", out var stats)
            || !stats.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array
        )
            return null;

        foreach (var page in pages.EnumerateArray())
        {
            if (
                !TryLong(page, "number", out var number)
                || number != 1
                || !page.TryGetProperty("table", out var table)
                || table.ValueKind != JsonValueKind.Array
            )
                continue;

            foreach (var entry in table.EnumerateArray())
            {
                if (
                    entry.TryGetProperty("name", out var name)
                    && name.GetString() == "Logical Sectors Written"
                    && TryLong(entry, "value", out var value)
                    && !(
                        entry.TryGetProperty("flags", out var flags)
                        && flags.TryGetProperty("valid", out var valid)
                        && valid.ValueKind == JsonValueKind.False
                    )
                )
                    return value;
            }
        }
        return null;
    }

    private static bool TryLong(JsonElement parent, string property, out long value)
    {
        value = 0;
        return parent.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }

    private static long? Plausible(long count, long unit)
    {
        if (count < 0 || unit <= 0 || count > Implausible / unit)
            return null;
        return count * unit;
    }
}
