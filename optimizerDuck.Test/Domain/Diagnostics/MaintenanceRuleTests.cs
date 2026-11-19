using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Services.Diagnostics;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Domain.Diagnostics;

public class WingetOutputTests
{
    // Shape of `winget upgrade` on Diego's PC (19/11/2026), shortened. Names are generic here.
    private const string Table = """
        Name                    Id                       Version   Available  Source
        -------------------------------------------------------------------------------
        Git                     Git.Git                  2.54.0    2.56.0.4   winget
        Some Tool 7.42.1        Vendor.SomeTool          7.42.1    7.44.0     winget
        Launcher                Python.Launcher          < 3.14.7  3.15.2     winget
        3 upgrades available.
        """;

    [Fact]
    public void CountsTheRows_NotTheTranslatedSummary()
    {
        Assert.Equal(3, MaintenanceProbes.CountUpgradeRows(Table));
        Assert.Equal(
            3,
            MaintenanceProbes.CountUpgradeRows(
                Table.Replace("3 upgrades available.", "3 actualizaciones disponibles.")
            )
        );
    }

    [Fact]
    public void SpinnerAndCarriageReturns_DoNotCount()
    {
        Assert.Equal(
            3,
            MaintenanceProbes.CountUpgradeRows("   - \r   \\ \r" + Table.Replace("\n", "\r\n"))
        );
    }

    [Fact]
    public void NoTable_IsZero_ButAnErrorIsUnreadable()
    {
        Assert.Equal(
            0,
            MaintenanceProbes.CountUpgradeRows(
                "No installed package found matching input criteria."
            )
        );
        Assert.Null(
            MaintenanceProbes.CountUpgradeRows(
                "Failed when searching source: winget\r\nAn unexpected error occurred while executing the command: 0x8a15000f"
            )
        );
        Assert.Null(MaintenanceProbes.CountUpgradeRows(""));
    }
}

public class MaintenanceRuleTests
{
    private static DiagnosticSnapshot With(Measured<int> upgrades, Measured<PuaMode> pua) =>
        DiegoPc() with
        {
            Maintenance = new MaintenanceSnapshot
            {
                WingetUpgrades = upgrades,
                PuaProtection = pua,
            },
        };

    [Theory]
    [InlineData(4, RuleOutcome.Pass)]
    [InlineData(5, RuleOutcome.Finding)]
    [InlineData(20, RuleOutcome.Finding)]
    public void AppsOutdated_FiresFromFive(int count, RuleOutcome expected)
    {
        var result = new AppsOutdatedRule().Evaluate(
            With(Measured<int>.Of(count), Measured<PuaMode>.Of(PuaMode.On)),
            Refs
        );
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void AppsOutdated_WithoutWinget_IsNotMeasurable()
    {
        var result = new AppsOutdatedRule().Evaluate(
            With(Measured<int>.Missing(NotMeasuredReason.RequiresExternalTool), default),
            Refs
        );
        Assert.Equal(RuleOutcome.NotMeasurable, result.Outcome);
    }

    [Theory]
    [InlineData(PuaMode.Off, RuleOutcome.Finding)]
    [InlineData(PuaMode.Audit, RuleOutcome.Finding)]
    [InlineData(PuaMode.On, RuleOutcome.Pass)]
    public void PuaOff_FiresWhenNotBlocking(PuaMode mode, RuleOutcome expected)
    {
        var result = new PuaOffRule().Evaluate(
            With(Measured<int>.Of(0), Measured<PuaMode>.Of(mode)),
            Refs
        );
        Assert.Equal(expected, result.Outcome);
        if (expected == RuleOutcome.Finding)
            Assert.Equal([ActionIds.PuaOn, ActionIds.DefenderFull], result.Finding!.ActionIds);
    }

    [Fact]
    public void PuaOff_WithAnotherAntivirus_DoesNotApply()
    {
        var result = new PuaOffRule().Evaluate(
            With(Measured<int>.Of(0), Measured<PuaMode>.Missing(NotMeasuredReason.NotApplicable)),
            Refs
        );
        Assert.Equal(RuleOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public void PuaRevert_NeverTurnsTheBlockingOff()
    {
        // Diego's decision (19/11/2026): reverting goes to audit mode, never to off.
        Assert.Equal(
            "Set-MpPreference -PUAProtection AuditMode",
            SecurityAndPrivacy.EnablePuaProtection.RevertCommand
        );
    }

    [Fact]
    public void BothRules_AreRegistered()
    {
        Assert.Contains(DiagnosticRules.All, r => r.RuleId == RuleIds.AppsOutdated);
        Assert.Contains(DiagnosticRules.All, r => r.RuleId == RuleIds.PuaOff);
    }
}

public class UpkeepRuleTests
{
    private static DiagnosticSnapshot With(MaintenanceSnapshot m) =>
        DiegoPc() with
        {
            Maintenance = m,
        };

    private static MaintenanceSnapshot Diego => DiegoPc().Maintenance;

    [Theory]
    [InlineData(59, RuleOutcome.Finding)]
    [InlineData(60, RuleOutcome.Pass)]
    public void Battery_UnderSixtyPercent(int percent, RuleOutcome expected)
    {
        var r = new BatteryRule().Evaluate(
            With(Diego with { BatteryCapacityPercent = Measured<int>.Of(percent) }),
            Refs
        );
        Assert.Equal(expected, r.Outcome);
    }

    [Fact]
    public void Battery_OnADesktop_DoesNotApply()
    {
        Assert.Equal(
            RuleOutcome.NotApplicable,
            new BatteryRule().Evaluate(DiegoPc(), Refs).Outcome
        );
    }

    [Theory]
    [InlineData(100, 1000, RuleOutcome.Finding)]
    [InlineData(1000, 1000, RuleOutcome.Pass)]
    [InlineData(100, 100, RuleOutcome.Pass)] // the card cannot do more
    public void Network_At100_OnAGigabitCard(int link, int max, RuleOutcome expected)
    {
        var r = new Net100MRule().Evaluate(
            With(
                Diego with
                {
                    WiredLinkMbps = Measured<int>.Of(link),
                    WiredMaxMbps = Measured<int>.Of(max),
                }
            ),
            Refs
        );
        Assert.Equal(expected, r.Outcome);
    }

    [Theory]
    [InlineData(80.0, RuleOutcome.Pass)]
    [InlineData(80.5, RuleOutcome.Finding)]
    public void RamAtRest_OverEightyPercent(double percent, RuleOutcome expected)
    {
        var r = new RamIdleHighRule().Evaluate(
            With(Diego with { IdleRamPercent = Measured<double>.Of(percent) }),
            Refs
        );
        Assert.Equal(expected, r.Outcome);
    }

    [Fact]
    public void RamAtRest_WithoutASessionMeasurement_IsNotMeasurable()
    {
        var r = new RamIdleHighRule().Evaluate(
            With(
                Diego with
                {
                    IdleRamPercent = Measured<double>.Missing(NotMeasuredReason.NotReported),
                }
            ),
            Refs
        );
        Assert.Equal(RuleOutcome.NotMeasurable, r.Outcome);
    }

    [Theory]
    [InlineData(0, false, RuleOutcome.Finding)]
    [InlineData(0, true, RuleOutcome.Pass)]
    [InlineData(2, false, RuleOutcome.Pass)]
    public void Backup_NoPointsAndNoOneDrive(int points, bool oneDrive, RuleOutcome expected)
    {
        var r = new BackupNoneRule().Evaluate(
            With(
                Diego with
                {
                    RestorePoints = Measured<int>.Of(points),
                    OneDriveConfigured = Measured<bool>.Of(oneDrive),
                }
            ),
            Refs
        );
        Assert.Equal(expected, r.Outcome);
    }
}
