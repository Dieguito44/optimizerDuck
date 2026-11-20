using System.Globalization;
using System.IO;
using Microsoft.Win32;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.Reports;

namespace optimizerDuck.Test.Services.Reports;

/// <summary>Plan of improvements F3-04: report fingerprints and their check.</summary>
public sealed class ReportIntegrityTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"zentra-integrity-{Guid.NewGuid():N}"
    );

    public ReportIntegrityTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Sha256_IsTheStandardOne()
    {
        // The same value Get-FileHash and sha256sum give for "abc".
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ReportIntegrity.Sha256Of(Write("abc.txt", "abc"))
        );
    }

    [Fact]
    public void UntouchedFiles_Pass_ChangedOrMissingOnes_DoNot()
    {
        var pdf = Write("Informe.pdf", "%PDF report");
        var json = Write("session.json", "{ }");
        ReportIntegrity.WriteManifest(_folder, [pdf, json]);

        Assert.All(
            ReportIntegrity.Verify(_folder)!,
            c => Assert.Equal(IntegrityStatus.Ok, c.Status)
        );

        File.AppendAllText(pdf, " edited");
        File.Delete(json);
        var checks = ReportIntegrity.Verify(Path.Combine(_folder, ReportIntegrity.ManifestName))!;
        Assert.Equal(IntegrityStatus.Altered, checks.Single(c => c.File == "Informe.pdf").Status);
        Assert.Equal(IntegrityStatus.Missing, checks.Single(c => c.File == "session.json").Status);
    }

    [Fact]
    public void ManifestLinesWithPaths_AreNeverFollowed()
    {
        var hash = new string('a', 64);
        File.WriteAllText(
            Path.Combine(_folder, ReportIntegrity.ManifestName),
            $"{hash}  ..\\..\\Windows\\win.ini\n{hash}  C:\\x.txt\nnot a line\n"
        );
        Assert.All(
            ReportIntegrity.Verify(_folder)!,
            c => Assert.Equal(IntegrityStatus.Altered, c.Status)
        );
    }

    [Fact]
    public void NoManifest_IsNull()
    {
        Assert.Null(ReportIntegrity.Verify(_folder));
    }
}

/// <summary>Plan of improvements F3-06: the software inventory.</summary>
public class SoftwareInventoryTests
{
    private static readonly CultureInfo Spanish = new("es-CO");

    private static WindowsLicense Windows(bool? activated) =>
        new(
            "Professional",
            "25H2",
            26200,
            activated is { } a
                ? Measured<bool>.Of(a)
                : Measured<bool>.Missing(NotMeasuredReason.QueryFailed)
        );

    [Fact]
    public void Csv_HasHeaderWindowsLineAndOneLinePerProgram()
    {
        var entries = new[]
        {
            new InventoryEntry(
                "7-Zip 24.08 (x64)",
                "24.08",
                "Igor Pavlov",
                new DateOnly(2026, 3, 1),
                5632,
                InstallScope.Machine64
            ),
            new InventoryEntry("Spotify", null, null, null, null, InstallScope.User),
        };

        var lines = SoftwareInventory
            .ToCsv(entries, Windows(true), Spanish)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        var sep = Spanish.TextInfo.ListSeparator;
        Assert.StartsWith("Windows Professional" + sep + "25H2 26200", lines[1]);
        Assert.Contains(
            "7-Zip 24.08 (x64)" + sep + "24.08" + sep + "Igor Pavlov" + sep + "2026-03-01",
            lines[2]
        );
        Assert.Contains("5" + Spanish.NumberFormat.NumberDecimalSeparator + "5", lines[2]); // 5632 KB
    }

    [Theory]
    [InlineData(true, "Inventory.Windows.Activated")]
    [InlineData(false, "Inventory.Windows.NotActivated")]
    [InlineData(null, "Inventory.Windows.Unknown")]
    public void Csv_SaysWhetherWindowsIsActivated(bool? activated, string key)
    {
        var csv = SoftwareInventory.ToCsv([], Windows(activated), Spanish);
        Assert.Contains(optimizerDuck.Services.Configuration.Loc.Instance[key], csv);
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")] // would run as a formula in Excel
    [InlineData("+SUM(A1)", "'+SUM(A1)")]
    [InlineData("@evil", "'@evil")]
    [InlineData("A;B", "\"A;B\"")]
    [InlineData("Say \"hi\"", "\"Say \"\"hi\"\"\"")]
    [InlineData("Normal", "Normal")]
    [InlineData(null, "")]
    public void Cells_AreQuotedAndFormulasDefused(string? value, string expected)
    {
        Assert.Equal(expected, SoftwareInventory.Cell(value, ";"));
    }

    [Fact]
    public void Entries_WithoutSize_AreStillInTheInventory()
    {
        var path = $@"Software\ZentraTest\{Guid.NewGuid():N}";
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.SetValue("DisplayName", "  Programa sin tamaño ");
            key.SetValue("DisplayVersion", "1.2");
            key.SetValue("Publisher", "Editor");
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            var entry = InstalledAppsReader.ReadEntry(key, InstallScope.User)!;
            Assert.Equal("Programa sin tamaño", entry.Name);
            Assert.Equal("1.2", entry.Version);
            Assert.Null(entry.SizeKb);
            Assert.Null(InstalledAppsReader.Read(key)); // the heavy-apps list still needs a size
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\ZentraTest",
                throwOnMissingSubKey: false
            );
        }
    }
}
