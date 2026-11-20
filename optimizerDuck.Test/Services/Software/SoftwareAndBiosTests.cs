using optimizerDuck.Services.Bios;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Software;
using optimizerDuck.Services.System;

namespace optimizerDuck.Test.Services.Software;

public class SoftwareCatalogTests
{
    [Fact]
    public void Catalog_IsTheClosedList()
    {
        Assert.Equal(
            [
                "7zip.7zip",
                "VideoLAN.VLC",
                "TheDocumentFoundation.LibreOffice",
                "Google.Chrome",
                "Intel.IntelDriverAndSupportAssistant",
            ],
            SoftwareCatalog.Packages.Select(p => p.WingetId)
        );
    }

    [Fact]
    public void InstallCommand_IsExactlyThePlans()
    {
        var vlc = SoftwareCatalog.Packages.Single(p => p.WingetId == "VideoLAN.VLC");
        Assert.Equal(
            "install --id VideoLAN.VLC -e --silent --accept-package-agreements --accept-source-agreements",
            SoftwareCatalog.InstallArguments(vlc)
        );
        Assert.Equal(
            "uninstall --id VideoLAN.VLC -e --silent --accept-source-agreements",
            SoftwareCatalog.UninstallArguments(vlc)
        );
    }

    [Fact]
    public void IntelAssistant_IsOfferedOnlyOnIntelPcs()
    {
        var amd = SystemInfo.Unknown with
        {
            Cpu = SystemInfo.Unknown.Cpu with { Vendor = CpuVendor.Amd },
        };
        var intel = SystemInfo.Unknown with
        {
            Cpu = SystemInfo.Unknown.Cpu with { Vendor = CpuVendor.Intel },
        };

        Assert.DoesNotContain(SoftwareCatalog.For(amd), p => p.Target == SoftwareTarget.Intel);
        Assert.Contains(
            SoftwareCatalog.For(intel),
            p => p.WingetId == "Intel.IntelDriverAndSupportAssistant"
        );
        Assert.Equal(4, SoftwareCatalog.For(amd).Count);
    }

    [Fact]
    public void EveryPackage_HasANameAndDescription()
    {
        Assert.All(
            SoftwareCatalog.Packages,
            p =>
            {
                Assert.NotEqual($"Software.{p.Key}.Name", p.Name);
                Assert.NotEqual($"Software.{p.Key}.Description", p.Description);
            }
        );
    }
}

public class BiosGuideTests
{
    [Theory]
    [InlineData("ASRock", "B450M-HDV R4.0", true)]
    [InlineData(null, "B450M-HDV R4.0", true)]
    [InlineData("ASRock", "B450M Pro4", false)]
    [InlineData("ASUSTeK COMPUTER INC.", "B450M-HDV R4.0", false)]
    [InlineData("ASRock", null, false)]
    public void Guide_MatchesOnlyItsBoard(string? manufacturer, string? board, bool expected)
    {
        Assert.Equal(expected, BiosGuides.AsrockB450mHdvR40.Matches(manufacturer, board));
    }

    [Fact]
    public void DocumentedSteps_CiteAManualPage_UndocumentedOnesDoNot()
    {
        foreach (var step in BiosGuides.All.SelectMany(g => g.Steps))
            Assert.Equal(step.IsDocumented, step.ManualPage is not null);
    }

    [Fact]
    public void XmpAndUma_AreMarkedNotDocumented_SoNoPathIsInvented()
    {
        var steps = BiosGuides.AsrockB450mHdvR40.Steps.ToDictionary(s => s.Key);
        Assert.Equal(BiosStepSource.NotDocumented, steps["Xmp"].Source);
        Assert.Equal(BiosStepSource.NotDocumented, steps["Uma"].Source);
    }

    [Fact]
    public void EveryStep_HasItsTexts()
    {
        foreach (var step in BiosGuides.All.SelectMany(g => g.Steps))
        {
            Assert.NotEqual(
                $"BiosGuide.{step.Key}.Title",
                Loc.Instance[$"BiosGuide.{step.Key}.Title"]
            );
            Assert.NotEqual(
                $"BiosGuide.{step.Key}.Body",
                Loc.Instance[$"BiosGuide.{step.Key}.Body"]
            );
        }
    }
}

public class BiosBoardNameTests
{
    // Version 1 test (20/11/2026): the BIOS page said "ASRock ASRock B450M-HDV R4.0".
    [Theory]
    [InlineData("ASRock", "ASRock B450M-HDV R4.0", "ASRock B450M-HDV R4.0")]
    [InlineData("ASUSTeK COMPUTER INC.", "PRIME B550M-A", "ASUSTeK COMPUTER INC. PRIME B550M-A")]
    [InlineData(null, "B450M", "B450M")]
    [InlineData("", "B450M", "B450M")]
    public void BoardName_DoesNotRepeatTheMaker(string? maker, string board, string expected) =>
        Assert.Equal(
            expected,
            optimizerDuck.UI.ViewModels.Pages.BiosGuideViewModel.BoardName(maker, board)
        );
}
