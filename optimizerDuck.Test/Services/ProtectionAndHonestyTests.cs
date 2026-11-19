using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Optimizations.Models.Bloatware;
using optimizerDuck.Services.Profiles;
using optimizerDuck.Services.Repair;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Test.Services;

/// <summary>
///     Plan of improvements, phase 2A: protected apps and tasks (F2-04) and results that do not
///     claim more than was measured (F2-07, audit Z-14, Z-15, Z-16, Z-24).
/// </summary>
public class ProtectionAndHonestyTests
{
    // ----- F2-04: apps -------------------------------------------------------------------

    [Fact]
    public void Photos_IsProtectedByItsRealPackageName()
    {
        Assert.True(Shared.IsProtectedApp("Microsoft.Windows.Photos"));
    }

    [Fact]
    public void Bloatware_OnlyProfileAppsAreSafe()
    {
        foreach (var app in StandardServiceProfile.Apps)
            Assert.Equal(AppRisk.Safe, BloatwareService.ZentraRisk(app, AppRisk.Unknown));
    }

    [Theory]
    [InlineData("Microsoft.ZuneVideo")] // Movies and TV: the plan says ask
    [InlineData("Microsoft.MicrosoftStickyNotes")] // the client's notes
    [InlineData("SpotifyAB.SpotifyMusic")]
    public void Bloatware_WhatTheOriginalCalledSafe_IsCautionInZentra(string package)
    {
        Assert.Equal(AppRisk.Caution, BloatwareService.ZentraRisk(package, AppRisk.Safe));
    }

    [Fact]
    public void Bloatware_UnknownAndCautionStayAsTheyWere()
    {
        Assert.Equal(AppRisk.Unknown, BloatwareService.ZentraRisk("Contoso.App", AppRisk.Unknown));
        Assert.Equal(AppRisk.Caution, BloatwareService.ZentraRisk("Contoso.App", AppRisk.Caution));
    }

    // ----- F2-04: scheduled tasks --------------------------------------------------------

    [Theory]
    [InlineData(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan")]
    [InlineData(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start")]
    [InlineData(@"Microsoft\Windows\WindowsUpdate\Scheduled Start")] // without the leading slash
    [InlineData(@"\microsoft\windows\windows defender\Windows Defender Scheduled Scan")]
    [InlineData(@"\Microsoft\Windows\SystemRestore\SR")]
    [InlineData(@"\Microsoft\Windows\BitLocker\BitLocker MDM policy Refresh")]
    [InlineData(@"\MicrosoftEdgeUpdateTaskMachineCore")]
    public void ProtectedTasks_AreRecognized(string path)
    {
        Assert.True(ScheduledTaskService.IsProtectedTask(path));
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser")]
    [InlineData(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator")]
    [InlineData(@"\Microsoft\Windows\Maps\MapsUpdateTask")]
    [InlineData(@"\GoogleUpdateTaskMachineCore")]
    [InlineData("")]
    public void OtherTasks_AreNotProtected(string path)
    {
        Assert.False(ScheduledTaskService.IsProtectedTask(path));
    }

    [Fact]
    public void ProtectedTask_IsNeverDeleted()
    {
        // The guard runs before the Task Scheduler is touched.
        var result = ScheduledTaskService.DeleteTask(
            null,
            @"\Microsoft\Windows\WindowsUpdate\Scheduled Start"
        );
        Assert.False(result.Ok);
    }

    // ----- F2-07: sfc tells the truth ----------------------------------------------------

    [Fact]
    public void Sfc_WithNothingRepaired_IsOkNotRepaired()
    {
        string[] lines =
        [
            "2026-11-19 10:00:00, Info  CSI 00000001 [SR] Verifying 100 components",
            "2026-11-19 10:05:00, Info  CSI 00000002 [SR] Verify complete",
        ];
        Assert.Equal(RepairOutcome.Ok, RepairService.SfcOutcome(0, lines));
    }

    [Fact]
    public void Sfc_RepairedAndUnrepairable_AreTold()
    {
        string[] repaired = ["2026-11-19 10:00:00, Info CSI 1 [SR] Repairing corrupted file x.dll"];
        string[] broken = ["2026-11-19 10:00:00, Info CSI 1 [SR] Cannot repair member file x.dll"];
        Assert.Equal(RepairOutcome.Repaired, RepairService.SfcOutcome(0, repaired));
        Assert.Equal(RepairOutcome.Problem, RepairService.SfcOutcome(0, broken));
        Assert.Equal(RepairOutcome.Failed, RepairService.SfcOutcome(1, repaired));
    }

    [Fact]
    public void Cbs_OnlyLinesOfThisRunCount()
    {
        string[] lines =
        [
            "2026-11-18 09:00:00, Info CSI 1 [SR] Repairing corrupted file old.dll",
            "garbage",
            "2026-11-19 10:00:01, Info CSI 2 [SR] Verify complete",
        ];
        var since = new DateTime(2026, 11, 19, 10, 0, 0);
        var mine = RepairService.LinesSince(lines, since);
        Assert.Single(mine);
        Assert.Equal(RepairOutcome.Ok, RepairService.SfcOutcome(0, mine));
    }
}
