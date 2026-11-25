using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.System;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Domain.Diagnostics;

/// <summary>Extended diagnosis (version 1.2.0).</summary>
public class HealthRuleTests
{
    private static HealthSnapshot Healthy => DiegoPc().Health;

    private static RuleResult Eval(IDiagnosticRule rule, HealthSnapshot health) =>
        rule.Evaluate(DiegoPc() with { Health = health }, Refs);

    // ---- CPU at rest ------------------------------------------------------------------------

    [Theory]
    [InlineData(3.2, null)]
    [InlineData(10.0, null)]
    [InlineData(14.5, Severity.Medium)]
    [InlineData(40.0, Severity.High)]
    public void CpuIdle_IsAFindingOverTenPercent(double percent, Severity? expected)
    {
        var result = Eval(
            new CpuIdleHighRule(),
            Healthy with
            {
                IdleCpuPercent = Measured<double>.Of(percent),
            }
        );
        Assert.Equal(expected, result.Finding?.Severity);
    }

    [Fact]
    public void CpuIdle_WithoutASession_IsNotMeasurable()
    {
        var result = Eval(new CpuIdleHighRule(), new HealthSnapshot());
        Assert.Equal(RuleOutcome.NotMeasurable, result.Outcome);
    }

    [Fact]
    public void CpuBusy_IsTheShareThatWasNotIdle()
    {
        // Kernel time includes idle: 1000 of kernel + 1000 of user, 1500 of them idle.
        var busy = MeasurementService.CpuTimes.BusyPercent(
            new MeasurementService.CpuTimes(0, 0, 0),
            new MeasurementService.CpuTimes(1500, 1000, 1000)
        );
        Assert.Equal(25.0, busy.ValueOrDefault);
        Assert.False(MeasurementService.CpuTimes.BusyPercent(null, null).HasValue);
    }

    // ---- Visual C++ and .NET ----------------------------------------------------------------

    [Fact]
    public void Runtimes_AllPresentAndRecent_Pass()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new RuntimesRule(), Healthy).Outcome);
    }

    [Fact]
    public void Runtimes_MissingVisualCpp_IsMedium_AndOldIsLow()
    {
        var missing = Eval(
            new RuntimesRule(),
            Healthy with
            {
                Runtimes = Measured<IReadOnlyList<RuntimeStatus>>.Of([
                    new("vcredist-x64", false, null),
                    new("vcredist-x86", true, "14.44.35211"),
                ]),
            }
        );
        Assert.Equal(Severity.Medium, missing.Finding!.Severity);
        Assert.Equal([ActionIds.Runtimes], missing.Finding.ActionIds);
        Assert.Contains(
            missing.Finding.Evidence,
            e => e.Text == "not_installed" && e.Subject == "Visual C++ 2015-2022 x64"
        );

        var old = Eval(
            new RuntimesRule(),
            Healthy with
            {
                Runtimes = Measured<IReadOnlyList<RuntimeStatus>>.Of([
                    new("vcredist-x64", true, "14.29.30133"),
                ]),
            }
        );
        Assert.Equal(Severity.Low, old.Finding!.Severity);
        Assert.Contains(old.Finding.Evidence, e => e.Text == "outdated");
    }

    // ---- Program crashes --------------------------------------------------------------------

    [Theory]
    [InlineData("msvcp140.dll", "vcruntime")]
    [InlineData("VCRUNTIME140_1.dll", "vcruntime")]
    [InlineData("ucrtbase.dll", "vcruntime")]
    [InlineData("nvwgf2umx.dll", "gpu")]
    [InlineData("atiumd64.dll", "gpu")]
    [InlineData("igd10iumd64.dll", "gpu")]
    [InlineData("d3d11.dll", "gpu")]
    [InlineData("coreclr.dll", "dotnet")]
    [InlineData("KERNELBASE.dll", "system")]
    [InlineData("game.exe", "other")]
    [InlineData("", "other")]
    public void CrashModules_AreGroupedByKind(string module, string group)
    {
        Assert.Equal(group, AppCrashesRule.GroupOf(module));
    }

    [Fact]
    public void Crashes_AreSummarized_TopThreeAppsOnly()
    {
        var summary = HealthProbes.Summarize([
            ("game.exe", "msvcp140.dll"),
            ("game.exe", "msvcp140.dll"),
            ("game.exe", "nvwgf2umx.dll"),
            ("editor.exe", "nvwgf2umx.dll"),
            ("chat.exe", "KERNELBASE.dll"),
            ("tool.exe", "tool.dll"),
        ]);
        Assert.Equal(6, summary.Total.ValueOrDefault);
        Assert.Equal(3, summary.Apps.Count);
        Assert.Equal(new CrashedApp("game.exe", 3), summary.Apps[0]);
        Assert.Equal(2, summary.Groups.Single(g => g.Group == "vcruntime").Count);
        Assert.Equal(2, summary.Groups.Single(g => g.Group == "gpu").Count);
    }

    [Fact]
    public void Crashes_UnderFive_Pass_AndTheAppNamesStayLocal()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new AppCrashesRule(), Healthy).Outcome);

        var result = Eval(
            new AppCrashesRule(),
            Healthy with
            {
                AppCrashes30d = Measured<int>.Of(9),
                CrashGroups = [new("vcruntime", 6), new("gpu", 1), new("other", 2)],
                CrashedApps = [new("game.exe", 7)],
            }
        );
        Assert.Equal(Severity.Low, result.Finding!.Severity);
        // Visual C++ failed 6 times; the video driver only once, not enough to blame it.
        Assert.Equal([ActionIds.Runtimes], result.Finding.ActionIds);
        Assert.All(
            result.Finding.Evidence.Where(e => e.Subject == "game.exe"),
            e => Assert.True(e.LocalOnly)
        );
    }

    // ---- Clock ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.4, null)]
    [InlineData(-90, null)]
    [InlineData(300, Severity.Medium)]
    [InlineData(-7200, Severity.High)]
    public void Clock_IsAFindingOverTwoMinutes(double seconds, Severity? expected)
    {
        var result = Eval(
            new ClockOffRule(),
            Healthy with
            {
                ClockOffsetSeconds = Measured<double>.Of(seconds),
            }
        );
        Assert.Equal(expected, result.Finding?.Severity);
    }

    [Fact]
    public void Sntp_ReadsTheOffset_AndRejectsABadReply()
    {
        var sent = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var received = sent.AddMilliseconds(100);
        // The server's clock is 30 s behind the PC: it got and answered the request at 11:59:30.05.
        var server = sent.AddSeconds(-30).AddMilliseconds(50);
        var reply = new byte[48];
        reply[0] = 0x1C; // version 3, server mode
        reply[1] = 2; // stratum
        WriteNtp(reply, 32, server);
        WriteNtp(reply, 40, server);

        Assert.Equal(30.0, HealthProbes.SntpOffsetSeconds(reply, sent, received));

        reply[1] = 0; // "kiss of death"
        Assert.Null(HealthProbes.SntpOffsetSeconds(reply, sent, received));
        Assert.Null(HealthProbes.SntpOffsetSeconds(new byte[10], sent, received));
    }

    private static void WriteNtp(byte[] data, int offset, DateTime utc)
    {
        var span = utc - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seconds = (ulong)span.TotalSeconds;
        var fraction = (ulong)((span.TotalSeconds - seconds) * 0x1_0000_0000);
        for (var i = 0; i < 4; i++)
        {
            data[offset + i] = (byte)(seconds >> (24 - 8 * i));
            data[offset + 4 + i] = (byte)(fraction >> (24 - 8 * i));
        }
    }

    // ---- Driver dumps -----------------------------------------------------------------------

    [Theory]
    [InlineData(300L, null)]
    [InlineData(2048L, Severity.Low)]
    [InlineData(8192L, Severity.Medium)]
    public void Dumps_AreAFindingOverOneGigabyte(long megabytes, Severity? expected)
    {
        var result = Eval(
            new DumpsBigRule(),
            Healthy with
            {
                LiveKernelReportsBytes = Measured<long>.Of(megabytes * 1024 * 1024),
            }
        );
        Assert.Equal(expected, result.Finding?.Severity);
        if (expected is not null)
            Assert.Equal([ActionIds.DiskCleanup], result.Finding!.ActionIds);
    }

    // ---- SSD maker tools --------------------------------------------------------------------

    [Theory]
    [InlineData("Samsung SSD 980 PRO 1TB", "Samsung Magician")]
    [InlineData("WDC WDS500G2B0A-00SM50", "WD Dashboard")]
    [InlineData("WD Blue SN570 1TB", "WD Dashboard")]
    [InlineData("SanDisk SSD PLUS 480GB", "SanDisk Dashboard")]
    [InlineData("CT500P3SSD8", "Crucial Storage Executive")]
    [InlineData("KINGSTON SA400S37480G", "Kingston SSD Manager")]
    [InlineData("INTEL SSDPEKNW512G8", "Solidigm Storage Tool")]
    [InlineData("ADATA SU650", "ADATA SSD ToolBox")]
    [InlineData("Generic SSD 256GB", null)]
    public void SsdTool_IsTheMakersOwn(string model, string? tool)
    {
        Assert.Equal(tool, SsdToolRule.ToolFor(model));
    }

    [Fact]
    public void SsdTool_IsInformation_OnlyForSsds()
    {
        var snapshot = DiegoPc();
        var result = new SsdToolRule().Evaluate(snapshot, Refs);
        Assert.Equal(Severity.Info, result.Finding!.Severity);
        Assert.Equal(2, result.Finding.Evidence.Count); // Crucial P3 and Kingston A400

        var hddOnly = snapshot with
        {
            Disks = [Disk(0, "ST1000DM010-2EP102", StorageMediaType.Hdd, isSystem: true)],
        };
        Assert.Equal(RuleOutcome.NotApplicable, new SsdToolRule().Evaluate(hddOnly, Refs).Outcome);
    }

    // ---- Antivirus --------------------------------------------------------------------------

    [Theory]
    [InlineData(397568u, true, true)] // 0x061100: on, up to date
    [InlineData(393472u, false, true)] // 0x060100: off
    [InlineData(397584u, true, false)] // 0x061110: on, out of date
    public void SecurityCenterState_IsDecoded(uint state, bool enabled, bool upToDate)
    {
        Assert.Equal((enabled, upToDate), AntivirusRule.Decode(state));
    }

    [Fact]
    public void Antivirus_NoneOn_IsHigh_OutOfDate_AndTwoAtOnce_AreMedium()
    {
        static Severity? Of(params AntivirusEntry[] products) =>
            Eval(
                new AntivirusRule(),
                Healthy with
                {
                    Antivirus = Measured<IReadOnlyList<AntivirusEntry>>.Of(products),
                }
            ).Finding?.Severity;

        Assert.Null(Of(new AntivirusEntry("Microsoft Defender Antivirus", true, true, true)));
        Assert.Equal(Severity.High, Of());
        Assert.Equal(
            Severity.High,
            Of(new AntivirusEntry("Microsoft Defender Antivirus", false, true, true))
        );
        Assert.Equal(Severity.Medium, Of(new AntivirusEntry("Some AV", true, false, false)));
        Assert.Equal(
            Severity.Medium,
            Of(
                new AntivirusEntry("Some AV", true, true, false),
                new AntivirusEntry("Other AV", true, true, false)
            )
        );
    }

    [Fact]
    public void Antivirus_OnWindowsServer_IsNotMeasurable()
    {
        var result = Eval(
            new AntivirusRule(),
            Healthy with
            {
                Antivirus = Measured<IReadOnlyList<AntivirusEntry>>.Missing(
                    NotMeasuredReason.NotApplicable
                ),
            }
        );
        Assert.Equal(RuleOutcome.NotMeasurable, result.Outcome);
    }

    // ---- Leaks ------------------------------------------------------------------------------

    [Fact]
    public void Leak_IsMedium_AndTheProgramNameStaysLocal()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new LeakRule(), Healthy).Outcome);

        var result = Eval(
            new LeakRule(),
            Healthy with
            {
                LeakingProcesses = Measured<IReadOnlyList<LeakingProcess>>.Of([
                    new("app.exe", 150_000, 200),
                    new("viewer.exe", 3_000, 9_500),
                ]),
            }
        );
        Assert.Equal(Severity.Medium, result.Finding!.Severity);
        Assert.Equal([ActionIds.HeavyApps], result.Finding.ActionIds);
        Assert.All(
            result.Finding.Evidence.Where(e => e.Subject is not null),
            e => Assert.True(e.LocalOnly)
        );
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "leak.gdi" && e.Subject == "viewer.exe"
        );
    }

    // ---- Boot in two parts ------------------------------------------------------------------

    [Fact]
    public void SlowBoot_SaysHowLongUpToTheDesktop_AndLoadingPrograms()
    {
        var snapshot = DiegoPc() with
        {
            BootMedianMs = Measured<int>.Of(95_000),
            BootDesktopMs = Measured<int>.Of(35_000),
            BootAfterDesktopMs = Measured<int>.Of(60_000),
        };
        var result = new BootSlowRule().Evaluate(snapshot, Refs);
        Assert.Contains(
            result.Finding!.Evidence,
            e => e.Key == "boot.main_path_s" && e.Number == 35m
        );
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "boot.post_boot_s" && e.Number == 60m
        );
    }
}
