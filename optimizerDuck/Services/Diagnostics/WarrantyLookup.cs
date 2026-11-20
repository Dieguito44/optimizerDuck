using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

public sealed record WarrantyInfo(
    string? Manufacturer,
    string? Model,
    string? Serial,
    string? LookupUrl
);

/// <summary>
///     Plan of improvements F4-07: the manufacturer's warranty. Their APIs need keys, so Zentra
///     does not query them: it shows the brand, model and serial number, copies the serial and
///     opens the manufacturer's own warranty page, where the technician pastes it. The serial is
///     not put in the address, nor in reports, nor sent to the AI.
/// </summary>
public static class WarrantyLookup
{
    /// <summary>Official warranty pages (checked 2026-11-19; Dell blocks automated checks).</summary>
    private static readonly (string Brand, string Url)[] Pages =
    [
        ("Lenovo", "https://pcsupport.lenovo.com/warrantylookup"),
        ("HP", "https://support.hp.com/us-en/check-warranty"),
        ("Hewlett-Packard", "https://support.hp.com/us-en/check-warranty"),
        ("Dell", "https://www.dell.com/support/home/en-us"),
        ("ASUS", "https://www.asus.com/support/warranty-status-inquiry/"),
    ];

    /// <summary>What boards of PCs built by parts report instead of a serial number.</summary>
    private static readonly string[] Placeholders =
    [
        "to be filled",
        "default string",
        "system serial number",
        "not applicable",
        "none",
        "n/a",
        "o.e.m",
        "0123456789",
        "123456789",
    ];

    public static WarrantyInfo Read()
    {
        var serial = WmiHelper.Query(
            "SELECT SerialNumber FROM Win32_BIOS",
            static items =>
                items.Select(mo => WmiHelper.GetString(mo, "SerialNumber")).FirstOrDefault()
        );
        var system = WmiHelper.Query(
            "SELECT Manufacturer, Model FROM Win32_ComputerSystem",
            static items =>
                items
                    .Select(mo =>
                        (WmiHelper.GetString(mo, "Manufacturer"), WmiHelper.GetString(mo, "Model"))
                    )
                    .FirstOrDefault()
        );
        return Build(system.Item1, system.Item2, serial);
    }

    internal static WarrantyInfo Build(string? manufacturer, string? model, string? serial)
    {
        var cleanSerial = IsRealSerial(serial) ? serial!.Trim() : null;
        var url = manufacturer is null
            ? null
            : Pages
                .Where(p => manufacturer.Contains(p.Brand, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Url)
                .FirstOrDefault();
        return new WarrantyInfo(Clean(manufacturer), Clean(model), cleanSerial, url);
    }

    internal static bool IsRealSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Trim().Length < 4)
            return false;
        var lower = serial.Trim().ToLowerInvariant();
        return !Placeholders.Any(lower.Contains) && lower.Distinct().Count() > 1;
    }

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text)
        || text.Contains("to be filled", StringComparison.OrdinalIgnoreCase)
            ? null
            : text.Trim();
}
