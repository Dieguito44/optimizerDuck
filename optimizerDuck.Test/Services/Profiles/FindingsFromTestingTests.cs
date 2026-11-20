using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Software;
using optimizerDuck.Test.TestDoubles;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>Fixes for what the tests on Diego's PC and in Windows Sandbox found (19/11/2026).</summary>
public class FindingsFromTestingTests
{
    private sealed class Irreversible : StubOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        ) => Task.FromResult(ApplyResult.True());
    }

    private static ChangeRecord Record(string outcome, DateTime? revertedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            LogName = "ResetBase",
            OptimizationKey = "DismResetBase",
            AppliedAt = new DateTime(2026, 11, 19, 22, 17, 0),
            Outcome = outcome,
            RevertedAt = revertedAt,
        };

    [Fact]
    public void AnIrreversibleSettingThatRan_SaysItIsDone()
    {
        var reset = new Irreversible { Confirmation = OptimizationConfirmation.Irreversible };
        Assert.NotNull(OptimizationService.DoneIrreversibleText(reset, false, Record("Success")));
        Assert.Null(OptimizationService.DoneIrreversibleText(reset, false, Record("Failed")));
        Assert.Null(OptimizationService.DoneIrreversibleText(reset, false, null));

        var normal = new Irreversible();
        Assert.Null(OptimizationService.DoneIrreversibleText(normal, false, Record("Success")));
    }

    [Fact]
    public void EachProgram_KeepsItsOwnHistoryRecord()
    {
        var sevenZip = SoftwareService.SubjectFor(
            new SoftwarePackage("7zip.7zip", "SevenZip", "LGPL-2.1", default)
        );
        var vlc = SoftwareService.SubjectFor(
            new SoftwarePackage("VideoLAN.VLC", "Vlc", "GPL-2.0", default)
        );

        Assert.NotEqual(sevenZip.Id, vlc.Id);
        Assert.NotEqual(SoftwareService.Subject.Id, sevenZip.Id);
        // The same program always lands in the same record.
        Assert.Equal(
            sevenZip.Id,
            SoftwareService
                .SubjectFor(new SoftwarePackage("7zip.7zip", "SevenZip", "LGPL-2.1", default))
                .Id
        );
    }
}
