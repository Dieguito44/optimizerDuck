using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.Diagnostics;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Domain.Diagnostics;

/// <summary>Plan of improvements, block 2B: read-only diagnostics (F2-11 to F2-14).</summary>
public class PlatformRuleTests
{
    private static RuleResult Eval(IDiagnosticRule rule, DiagnosticSnapshot snapshot) =>
        rule.Evaluate(snapshot, Refs);

    // ----- R-SECURE-PLATFORM -------------------------------------------------------------

    [Fact]
    public void DiegoPc_SecureBootAndTpm2_Pass()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new SecurePlatformRule(), DiegoPc()).Outcome);
    }

    [Theory]
    [InlineData(false, 2, Severity.Medium)] // Secure Boot off
    [InlineData(true, 0, Severity.Low)] // no TPM
    [InlineData(true, 1, Severity.Low)] // TPM 1.2
    [InlineData(false, 0, Severity.Medium)] // both: the worst wins
    public void SecureBootOffOrNoTpm2_IsFound(bool secureBoot, int tpm, Severity expected)
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Os = pc.Os with { SecureBoot = Measured<bool>.Of(secureBoot) },
            Platform = pc.Platform with { TpmVersion = Measured<int>.Of(tpm) },
        };
        var result = Eval(new SecurePlatformRule(), snapshot);
        Assert.Equal(expected, result.Finding!.Severity);
        Assert.Equal([ActionIds.BiosSecureBoot], result.Finding.ActionIds);
    }

    // ----- R-UEFI-CA2023 -----------------------------------------------------------------

    [Theory]
    [InlineData(2, RuleOutcome.Pass)]
    [InlineData(1, RuleOutcome.Pass)]
    [InlineData(0, RuleOutcome.Finding)]
    public void UefiCa2023_OnlyMissingIsFound(int capable, RuleOutcome expected)
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Platform = pc.Platform with { UefiCa2023 = Measured<int>.Of(capable) },
        };
        Assert.Equal(expected, Eval(new UefiCa2023Rule(), snapshot).Outcome);
    }

    [Fact]
    public void UefiCa2023_WithSecureBootOff_DoesNotApply()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Os = pc.Os with { SecureBoot = Measured<bool>.Of(false) },
            Platform = pc.Platform with { UefiCa2023 = Measured<int>.Of(0) },
        };
        Assert.Equal(RuleOutcome.NotApplicable, Eval(new UefiCa2023Rule(), snapshot).Outcome);
    }

    // ----- R-WU-HEALTH -------------------------------------------------------------------

    private static DiagnosticSnapshot WithUpdates(int days, int stuck, params string[] disabled) =>
        DiegoPc() with
        {
            Updates = new UpdateHealthSnapshot
            {
                DaysSinceSuccess = Measured<int>.Of(days),
                StuckUpdates = Measured<int>.Of(stuck),
                DisabledServices = Measured<IReadOnlyList<string>>.Of(disabled),
            },
        };

    [Fact]
    public void Updates_Healthy_Pass()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new UpdateHealthRule(), DiegoPc()).Outcome);
    }

    [Fact]
    public void Updates_DisabledService_IsHigh()
    {
        var result = Eval(new UpdateHealthRule(), WithUpdates(0, 0, "wuauserv"));
        Assert.Equal(Severity.High, result.Finding!.Severity);
        Assert.Contains(result.Finding.Evidence, e => e.Text == "wuauserv");
    }

    [Theory]
    [InlineData(0, 1)] // one update stuck
    [InlineData(45, 0)] // no Windows update for 45 days
    public void Updates_StuckOrOld_IsMedium(int days, int stuck)
    {
        Assert.Equal(
            Severity.Medium,
            Eval(new UpdateHealthRule(), WithUpdates(days, stuck)).Finding!.Severity
        );
    }

    [Fact]
    public void Updates_44DaysIsStillFine()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new UpdateHealthRule(), WithUpdates(44, 0)).Outcome);
    }

    // ----- Update history analysis -------------------------------------------------------

    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0);

    [Theory]
    [InlineData("2026-11 Actualización acumulativa para Windows 11 Version 25H2 (KB5068861)", true)]
    [InlineData(
        "Actualización de inteligencia de seguridad para Microsoft Defender Antivirus - KB2267602 (versión 1.463.368.0)",
        false
    )]
    [InlineData("9PLM9XGG6VKS-OpenAI.Codex", false)] // a Microsoft Store app
    [InlineData("9WZDNCRFJBMP-MICROSOFT.WINDOWSSTORE", false)]
    public void OnlyWindowsUpdatesWithAKbCount(string title, bool expected)
    {
        Assert.Equal(expected, PlatformProbes.IsWindowsUpdate(title));
    }

    [Fact]
    public void DiegoPcHistory_StoreFailuresAreIgnored()
    {
        // Like Diego's PC: many Store app failures, Windows updated this morning.
        var events = Enumerable
            .Range(0, 11)
            .Select(i => new UpdateEvent(Now.AddDays(-i), false, "9PLM9XGG6VKS-OpenAI.Codex"))
            .Append(
                new UpdateEvent(Now.AddHours(-5), true, "Actualización para Windows (KB5068861)")
            )
            .ToList();
        var history = PlatformProbes.AnalyzeUpdates(events, Now);
        Assert.Equal(0, history.DaysSinceKbSuccess);
        Assert.Equal(0, history.StuckKbUpdates);
    }

    [Fact]
    public void AnUpdateFailing3TimesWithoutSuccess_IsStuck_UnlessItLaterInstalled()
    {
        const string kb = "Actualización acumulativa (KB5070000)";
        var failures = Enumerable
            .Range(1, 3)
            .Select(i => new UpdateEvent(Now.AddDays(-i), false, kb));
        Assert.Equal(1, PlatformProbes.AnalyzeUpdates(failures, Now).StuckKbUpdates);

        var thenInstalled = failures.Append(new UpdateEvent(Now.AddHours(-1), true, kb));
        Assert.Equal(0, PlatformProbes.AnalyzeUpdates(thenInstalled, Now).StuckKbUpdates);
    }

    [Fact]
    public void NoWindowsUpdateInTheWindow_ReportsTheWholeWindow()
    {
        var history = PlatformProbes.AnalyzeUpdates([], Now);
        Assert.Equal(PlatformProbes.UpdateWindowDays, history.DaysSinceKbSuccess);
    }

    // ----- R-RESTORE-OFF -----------------------------------------------------------------

    [Fact]
    public void RestoreOff_IsMedium_AndOffersWeeklyRestorePoints()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Maintenance = pc.Maintenance with { SystemProtectionOn = Measured<bool>.Of(false) },
        };
        var result = Eval(new RestoreOffRule(), snapshot);
        Assert.Equal(Severity.Medium, result.Finding!.Severity);
        Assert.Equal([ActionIds.BackupSetup], result.Finding.ActionIds);
        Assert.Equal(RuleOutcome.Pass, Eval(new RestoreOffRule(), pc).Outcome);
    }

    // ----- Battery report ----------------------------------------------------------------

    [Fact]
    public void BatteryReport_ReadsCapacityAndCycles()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries>
                <Battery>
                  <Id>Battery</Id>
                  <SerialNumber>should-never-leave</SerialNumber>
                  <DesignCapacity>50000</DesignCapacity>
                  <FullChargeCapacity>27500</FullChargeCapacity>
                  <CycleCount>612</CycleCount>
                </Battery>
              </Batteries>
            </BatteryReport>
            """;
        var report = PlatformProbes.ParseBatteryReport(xml);
        Assert.Equal(55, report!.CapacityPercent);
        Assert.Equal(612, report.CycleCount);
    }

    [Fact]
    public void BatteryReport_WithoutBatteries_IsNotABatteryReport()
    {
        const string xml = """
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012"><Batteries /></BatteryReport>
            """;
        Assert.Null(PlatformProbes.ParseBatteryReport(xml));
        Assert.Null(PlatformProbes.ParseBatteryReport("not xml"));
    }

    [Fact]
    public void BatteryFinding_ShowsTheCycles()
    {
        var pc = DiegoPc();
        var laptop = pc with
        {
            Maintenance = pc.Maintenance with
            {
                BatteryCapacityPercent = Measured<int>.Of(55),
                BatteryCycles = Measured<int>.Of(612),
            },
        };
        var result = Eval(new BatteryRule(), laptop);
        Assert.Contains(
            result.Finding!.Evidence,
            e => e.Key == "battery.cycles" && e.Number == 612
        );
    }
}
