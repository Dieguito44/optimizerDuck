using System.IO;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Diagnostics;

/// <summary>Plan of improvements F2-09: host writes through smartctl, and SATA wear from TBW.</summary>
public class SmartctlProbeTests
{
    private const long GiB = 1L << 30;

    // ---- Parsing "smartctl -j -x" -------------------------------------------------------------

    [Fact]
    public void Nvme_DataUnitsAreThousandsOf512ByteSectors()
    {
        const string json = """
            { "nvme_smart_health_information_log": { "percentage_used": 3, "data_units_written": 20000000 } }
            """;
        Assert.Equal(20_000_000L * 512_000, SmartctlProbe.ParseHostWrites(json));
    }

    [Fact]
    public void Ata_DeviceStatistics_ComeFirst()
    {
        const string json = """
            {
              "in_smartctl_database": true,
              "logical_block_size": 512,
              "ata_device_statistics": { "pages": [
                { "number": 1, "name": "General Statistics", "table": [
                  { "offset": 8, "name": "Lifetime Power-On Resets", "value": 300 },
                  { "offset": 24, "name": "Logical Sectors Written", "value": 1000000, "flags": { "valid": true } }
                ] }
              ] },
              "ata_smart_attributes": { "table": [
                { "id": 241, "name": "Total_LBAs_Written", "raw": { "value": 5 } }
              ] }
            }
            """;
        Assert.Equal(1_000_000L * 512, SmartctlProbe.ParseHostWrites(json));
    }

    [Fact]
    public void Ata_InvalidDeviceStatistic_FallsBackToTheAttribute()
    {
        const string json = """
            {
              "ata_device_statistics": { "pages": [
                { "number": 1, "table": [
                  { "name": "Logical Sectors Written", "value": 0, "flags": { "valid": false } }
                ] }
              ] },
              "ata_smart_attributes": { "table": [
                { "id": 241, "name": "Lifetime_Writes_GiB", "raw": { "value": 12000 } }
              ] }
            }
            """;
        Assert.Equal(12_000 * GiB, SmartctlProbe.ParseHostWrites(json));
    }

    [Theory]
    [InlineData("Lifetime_Writes_GiB", 100, 100 * GiB)] // Kingston A400 (Phison S11)
    [InlineData("Host_Writes_GiB", 100, 100 * GiB)]
    [InlineData("Host_Writes_32MiB", 100, 100 * 32L * (1 << 20))]
    [InlineData("Total_Writes_GB", 100, 100_000_000_000L)]
    public void Ata_AttributeNamedWithItsUnit_IsConverted(string name, long raw, long bytes)
    {
        var json = $$"""
            { "ata_smart_attributes": { "table": [ { "id": 241, "name": "{{name}}", "raw": { "value": {{raw}} } } ] } }
            """;
        Assert.Equal(bytes, SmartctlProbe.ParseHostWrites(json));
    }

    [Theory]
    [InlineData(true, 4096L * 1000)]
    [InlineData(false, null)] // the default name alone does not prove the unit
    public void Ata_SectorCountedAttribute_NeedsTheDriveDatabase(bool inDatabase, long? bytes)
    {
        var json = $$"""
            {
              "in_smartctl_database": {{(inDatabase ? "true" : "false")}},
              "logical_block_size": 4096,
              "ata_smart_attributes": { "table": [ { "id": 241, "name": "Total_LBAs_Written", "raw": { "value": 1000 } } ] }
            }
            """;
        Assert.Equal(bytes, SmartctlProbe.ParseHostWrites(json));
    }

    [Fact]
    public void NandWrites_AreNeverTakenForHostWrites()
    {
        const string json = """
            { "in_smartctl_database": true, "ata_smart_attributes": { "table": [
              { "id": 233, "name": "Flash_Writes_GiB", "raw": { "value": 900 } },
              { "id": 249, "name": "NAND_Writes_1GiB", "raw": { "value": 900 } }
            ] } }
            """;
        Assert.Null(SmartctlProbe.ParseHostWrites(json));
    }

    [Theory]
    [InlineData(
        """{ "nvme_smart_health_information_log": { "data_units_written": 9223372036854775807 } }"""
    )]
    [InlineData(
        """{ "ata_smart_attributes": { "table": [ { "name": "Host_Writes_GiB", "raw": { "value": -1 } } ] } }"""
    )]
    [InlineData("""{ "smartctl": { "exit_status": 4 } }""")]
    public void ImplausibleOrMissing_IsNotMeasurable(string json)
    {
        Assert.Null(SmartctlProbe.ParseHostWrites(json));
    }

    // ---- Which disks, and the shipped file ----------------------------------------------------

    [Theory]
    [InlineData(4, 11, true)] // SATA SSD
    [InlineData(4, 17, true)] // NVMe
    [InlineData(0, 17, true)] // NVMe without a media type
    [InlineData(3, 11, false)] // HDD
    [InlineData(4, 7, false)] // SSD behind USB
    [InlineData(0, 11, false)] // unknown
    public void OnlyInternalSsdsAreRead(int mediaType, int busType, bool read)
    {
        var row = new DiskProbeRow { MediaType = mediaType, BusType = busType };
        Assert.Equal(read, SmartctlProbe.IsCandidate(row));
    }

    [Fact]
    public void ShippedSmartctl_HasTheExpectedHash()
    {
        var path = File.Exists(SmartctlProbe.ToolPath) ? SmartctlProbe.ToolPath : RepoCopy();
        using var file = File.OpenRead(path);
        Assert.True(SmartctlProbe.HasExpectedHash(file));
    }

    [Fact]
    public void AnyOtherFile_IsRefused()
    {
        using var file = new MemoryStream([0x4D, 0x5A, 0x90, 0x00]);
        Assert.False(SmartctlProbe.HasExpectedHash(file));
    }

    [Fact]
    public void ShippedSmartctl_ComesWithLicenseAndSource()
    {
        var folder = Path.GetDirectoryName(RepoCopy())!;
        Assert.Contains(
            "GNU GENERAL PUBLIC LICENSE",
            File.ReadAllText(Path.Combine(folder, "LICENSE.txt"))
        );
        var source = File.ReadAllText(Path.Combine(folder, "SOURCE.txt"));
        Assert.Contains("smartmontools-7.5.tar.gz", source);
        Assert.Contains(SmartctlProbe.ExpectedSha256, source);
    }

    // ---- The snapshot and R-SSD-WEAR ----------------------------------------------------------

    [Fact]
    public void SataWear_FromHostWritesAndTheTbwTable()
    {
        // KINGSTON SA400S37/480GB is rated 160 TBW. 136 TB written = 85 %: worn.
        var worn = DiegoPc() with
        {
            Disks =
            [
                Disk(0, "KINGSTON SA400S37/480GB", StorageMediaType.Ssd, wear: null) with
                {
                    BytesWritten = Measured<long>.Of(136_000_000_000_000L),
                },
            ],
        };
        var result = new SsdWearRule().Evaluate(worn, Refs);
        Assert.Equal(Severity.Critical, result.Finding!.Severity);
        Assert.Equal(85, Assert.Single(result.Finding.Evidence).Number);

        // 8 TB of 160 = 5 %: fine.
        var fine = worn with
        {
            Disks = [worn.Disks[0] with { BytesWritten = Measured<long>.Of(8_000_000_000_000L) }],
        };
        Assert.Equal(RuleOutcome.Pass, new SsdWearRule().Evaluate(fine, Refs).Outcome);
    }

    [Fact]
    public void SataWear_ModelNotInTheTable_IsNotGuessed()
    {
        var snapshot = DiegoPc() with
        {
            Disks =
            [
                Disk(0, "WDC WDS500G2B0A", StorageMediaType.Ssd, wear: null) with
                {
                    BytesWritten = Measured<long>.Of(400_000_000_000_000L),
                },
            ],
        };
        Assert.Equal(RuleOutcome.NotMeasurable, new SsdWearRule().Evaluate(snapshot, Refs).Outcome);
    }

    private static string RepoCopy()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = Path.Combine("optimizerDuck", "Resources", "Tools", "smartmontools");
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative, "smartctl.exe");
    }
}
