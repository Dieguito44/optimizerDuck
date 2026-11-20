using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;
using optimizerDuck.Services.UI;
using optimizerDuck.Test.TestDoubles;
using optimizerDuck.UI.Pages;
using optimizerDuck.UI.ViewModels.Pages;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>The redesign approved by Diego (19/11/2026): activity log, findings' targets, card states.</summary>
public class RedesignTests
{
    [Fact]
    public void TheActivityLog_HidesKeys_AndKeepsOnlyTheLastLines()
    {
        var marker = Guid.NewGuid().ToString("N");
        ActivityLog.Add($"{marker} clave sk-ant-api03-{new string('a', 40)}");
        var line = Assert.Single(ActivityLog.Entries, e => e.Text.Contains(marker));
        Assert.DoesNotContain("sk-ant-api03", line.Text);

        for (var i = 0; i < ActivityLog.MaxEntries + 5; i++)
            ActivityLog.Add($"línea {i}");
        Assert.True(ActivityLog.Entries.Count <= ActivityLog.MaxEntries);
    }

    [Fact]
    public async Task EveryRun_LeavesItsStartAndItsEnd_InTheActivityLog()
    {
        var name = "Prueba de bitácora " + Guid.NewGuid().ToString("N")[..6];
        var runner = TestRunner.New(
            new RevertManager(
                NullLogger<RevertManager>.Instance,
                TestShell.New(),
                new PowerPlanService(NullLogger<PowerPlanService>.Instance),
                TimeProvider.System
            )
        );

        await runner.RunAsync(
            new OperationRequest(
                new OperationSubject(Guid.NewGuid(), "RedesignTest", "Redesign test"),
                name,
                NullLogger.Instance,
                RevertPersistence.Disabled,
                (_, context) =>
                {
                    context.Changes.AddIrreversible("Shell", "test step");
                    return Task.FromResult(context.Changes.ToApplyResult());
                }
            ),
            new Progress<ProcessingProgress>(),
            TestContext.Current.CancellationToken
        );

        var lines = ActivityLog.Entries.Where(e => e.Text.StartsWith(name)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.NotEqual(ActivityKind.Error, lines[1].Kind);
    }

    [Theory]
    [InlineData(ActionIds.RepairWindows, typeof(RepairPage))]
    [InlineData(ActionIds.DiskCleanup, typeof(DiskCleanupPage))]
    [InlineData(ActionIds.StartupReview, typeof(StartupManagerPage))]
    [InlineData(ActionIds.PrivacyStandard, typeof(SessionPage))]
    [InlineData(ActionIds.BiosXmp, typeof(BiosGuidePage))]
    [InlineData(ActionIds.HardwareRamDual, typeof(DiagnosticPage))]
    public void EachFinding_OpensThePageWhereItIsFixed(string actionId, Type page)
    {
        Assert.Equal(page, OptimizeOverviewViewModel.TargetFor([actionId]));
    }

    [Fact]
    public void AToolCard_SaysWhatHappened()
    {
        var state = new ToolCardState();
        Assert.Equal(ToolCardStatus.Ready, state.Status);
        state.Start();
        Assert.Equal(ToolCardStatus.Running, state.Status);
        state.Finish(OptimizationSuccessResult.Success);
        Assert.Equal(ToolCardStatus.Done, state.Status);
        state.Finish(OptimizationSuccessResult.Failed);
        Assert.Equal(ToolCardStatus.Failed, state.Status);
        state.Finish(null); // the run threw
        Assert.Equal(ToolCardStatus.Failed, state.Status);
    }
}
