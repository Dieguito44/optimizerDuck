using Newtonsoft.Json;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Domain.Diagnostics;

/// <summary>What the display probe read on Diego's PC (Fable's probe, 19/11/2026).</summary>
internal static class DisplayFixtures
{
    public const long ApuLuid = 0xC3BA;

    public static DisplayProbeResult DiegoPcProbe(int hdmiHz = 120) =>
        new(
            [
                new RawAdapter(ApuLuid, 0x232B, 2, "AMD Radeon(TM) Graphics", 0x1002, false),
                new RawAdapter(0x1234, 0x105, 0, "Microsoft Basic Render Driver", 0x1414, true),
            ],
            [
                new RawMonitor
                {
                    AdapterLuid = ApuLuid,
                    SourceId = 0,
                    OutputTechnology = 5,
                    HasEdid = true,
                    FriendlyName = "Monitor HDMI",
                    GdiName = @"\\.\DISPLAY1",
                    MonitorDevicePath = @"\\?\DISPLAY#ABC#1",
                    CurrentWidth = 1920,
                    CurrentHeight = 1080,
                    CurrentHz = hdmiHz,
                    CurrentBpp = 32,
                    PreferredWidth = 1920,
                    PreferredHeight = 1080,
                    MaxHzAtPreferred = 120,
                },
                // The phantom DisplayPort output: no EDID, 1280x800.
                new RawMonitor
                {
                    AdapterLuid = ApuLuid,
                    SourceId = 1,
                    OutputTechnology = 10,
                    HasEdid = false,
                    GdiName = @"\\.\DISPLAY2",
                    CurrentWidth = 1280,
                    CurrentHeight = 800,
                    CurrentHz = 75,
                    CurrentBpp = 32,
                },
            ],
            false,
            false,
            false,
            Measured<int>.Of(0),
            Measured<int>.Of(0),
            SectionStatus.Ok
        );

    public static DiagnosticSnapshot Snapshot(
        DisplaySnapshot display,
        DeviceKind kind = DeviceKind.Desktop
    ) =>
        DiegoPc() with
        {
            Display = display,
            Os = DiegoPc().Os with { DeviceKind = Measured<DeviceKind>.Of(kind) },
        };

    public static DisplaySnapshot Map(DisplayProbeResult raw) =>
        DisplaySnapshotMapper.Map(raw, CpuVendor.Amd, "AMD Ryzen 5 5600GT with Radeon Graphics");
}

public class DisplayMapperTests
{
    [Fact]
    public void DiegoPc_OneHardwareAdapter_TwoMonitors_OnlyOneReal()
    {
        var d = DisplayFixtures.Map(DisplayFixtures.DiegoPcProbe());

        var adapter = Assert.Single(d.Adapters); // the Basic Render Driver is software
        Assert.True(adapter.IsIntegrated.ValueOrDefault); // the HybridIntegrated bit
        Assert.Equal(2, d.Monitors.Count);
        Assert.Single(d.Monitors, m => m.IsPhysical && m.HasEdid);
        Assert.All(d.Monitors, m => Assert.Equal(0, m.AdapterIndex.ValueOrDefault));
    }

    [Theory]
    [InlineData(5u, DisplayConnector.Hdmi)]
    [InlineData(10u, DisplayConnector.DisplayPort)]
    [InlineData(18u, DisplayConnector.DisplayPort)]
    [InlineData(11u, DisplayConnector.Internal)]
    [InlineData(0x80000000u, DisplayConnector.Internal)]
    [InlineData(17u, DisplayConnector.Virtual)]
    [InlineData(15u, DisplayConnector.Wireless)]
    public void Connectors_AreGrouped(uint tech, DisplayConnector expected)
    {
        Assert.Equal(expected, DisplaySnapshotMapper.MapConnector(tech));
    }

    [Fact]
    public void HybridBit_WinsOverTheName()
    {
        var dedicatedNamedLikeIgpu = new RawAdapter(
            1,
            0x10 | 0x3,
            2,
            "AMD Radeon(TM) Graphics",
            0x1002,
            false
        );
        Assert.False(
            DisplaySnapshotMapper.IsIntegrated(
                dedicatedNamedLikeIgpu,
                CpuVendor.Amd,
                "AMD Ryzen 5 5600GT"
            )
        );
    }

    [Fact]
    public void Snapshot_HasNoDevicePathsOrDisplayNames()
    {
        var json = JsonConvert.SerializeObject(DisplayFixtures.Map(DisplayFixtures.DiegoPcProbe()));
        Assert.DoesNotContain(@"\\", json);
        Assert.DoesNotContain("DISPLAY", json);
        Assert.DoesNotContain(".exe", json);
    }
}

public class MonitorHzRuleTests
{
    private static RuleResult Run(DisplayProbeResult raw) =>
        new MonitorHzRule().Evaluate(DisplayFixtures.Snapshot(DisplayFixtures.Map(raw)), Refs);

    [Fact]
    public void DiegoPc_At120_Passes_AndThePhantomOutputIsIgnored()
    {
        Assert.Equal(RuleOutcome.Pass, Run(DisplayFixtures.DiegoPcProbe()).Outcome);
    }

    [Theory]
    [InlineData(60, 144, RuleOutcome.Finding)]
    [InlineData(120, 144, RuleOutcome.Finding)]
    [InlineData(60, 75, RuleOutcome.Finding)]
    [InlineData(59, 60, RuleOutcome.Pass)]
    [InlineData(119, 120, RuleOutcome.Pass)]
    [InlineData(143, 144, RuleOutcome.Pass)]
    public void FiresOnlyFromTenHz(int current, int max, RuleOutcome expected)
    {
        var raw = DisplayFixtures.DiegoPcProbe();
        raw = raw with
        {
            Monitors = [raw.Monitors[0] with { CurrentHz = current, MaxHzAtPreferred = max }],
        };
        var result = Run(raw);
        Assert.Equal(expected, result.Outcome);
        if (expected == RuleOutcome.Finding)
            Assert.All(result.Finding!.Evidence, e => Assert.Equal("Monitor HDMI", e.Subject));
    }

    [Fact]
    public void NotAtNativeResolution_DoesNotFire()
    {
        var raw = DisplayFixtures.DiegoPcProbe();
        raw = raw with
        {
            Monitors =
            [
                raw.Monitors[0] with
                {
                    CurrentWidth = 1280,
                    CurrentHeight = 720,
                    CurrentHz = 60,
                    MaxHzAtPreferred = 144,
                },
            ],
        };
        Assert.Equal(RuleOutcome.Pass, Run(raw).Outcome);
    }

    [Fact]
    public void OnlyMonitorsWithoutEdid_DoNotApply()
    {
        var raw = DisplayFixtures.DiegoPcProbe();
        raw = raw with { Monitors = [raw.Monitors[1]] };
        Assert.Equal(RuleOutcome.NotApplicable, Run(raw).Outcome);
    }

    [Fact]
    public void RemoteDesktop_DoesNotApply()
    {
        Assert.Equal(
            RuleOutcome.NotApplicable,
            Run(DisplayFixtures.DiegoPcProbe() with { RemoteSession = true }).Outcome
        );
    }
}

public class MonitorPortRuleTests
{
    private const long Dgpu = 0xD1;

    private static DisplayProbeResult WithDedicated(bool monitorOnDedicated)
    {
        var raw = DisplayFixtures.DiegoPcProbe();
        return raw with
        {
            Adapters =
            [
                .. raw.Adapters,
                new RawAdapter(Dgpu, 0x13, 4, "NVIDIA GeForce RTX 3060", 0x10DE, false),
            ],
            Monitors =
            [
                raw.Monitors[0] with
                {
                    AdapterLuid = monitorOnDedicated ? Dgpu : DisplayFixtures.ApuLuid,
                },
            ],
        };
    }

    private static RuleResult Run(DisplayProbeResult raw, DeviceKind kind = DeviceKind.Desktop) =>
        new MonitorPortRule().Evaluate(
            DisplayFixtures.Snapshot(DisplayFixtures.Map(raw), kind),
            Refs
        );

    [Fact]
    public void MonitorOnTheMotherboard_WithAGraphicsCard_IsCritical()
    {
        var result = Run(WithDedicated(monitorOnDedicated: false));
        Assert.Equal(RuleOutcome.Finding, result.Outcome);
        Assert.Equal(Severity.Critical, result.Finding!.Severity);
    }

    [Fact]
    public void MonitorOnTheGraphicsCard_Passes()
    {
        Assert.Equal(RuleOutcome.Pass, Run(WithDedicated(monitorOnDedicated: true)).Outcome);
    }

    [Fact]
    public void Laptop_DoesNotApply()
    {
        Assert.Equal(
            RuleOutcome.NotApplicable,
            Run(WithDedicated(false), DeviceKind.Laptop).Outcome
        );
    }

    [Fact]
    public void OnlyIntegratedGraphics_DoesNotApply()
    {
        Assert.Equal(RuleOutcome.NotApplicable, Run(DisplayFixtures.DiegoPcProbe()).Outcome);
    }

    [Fact]
    public void ComputeOnlyCard_DoesNotApply()
    {
        var raw = WithDedicated(false);
        raw = raw with
        {
            Adapters =
            [
                raw.Adapters[0],
                raw.Adapters[1],
                raw.Adapters[2] with
                {
                    TypeBits = 0x1 | (1 << 11),
                    Sources = 0,
                },
            ],
        };
        Assert.Equal(RuleOutcome.NotApplicable, Run(raw).Outcome);
    }
}

public class GpuHybridRuleTests
{
    private static DisplaySnapshot Hybrid(bool effective, int withoutPreference) =>
        new()
        {
            Adapters =
            [
                new DisplayAdapterSnapshot { Index = 0, IsIntegrated = Measured<bool>.Of(true) },
                new DisplayAdapterSnapshot { Index = 1, IsIntegrated = Measured<bool>.Of(false) },
            ],
            GpuPreferenceEffective = Measured<bool>.Of(effective),
            GamesDetected = Measured<int>.Of(3),
            GamesWithoutGpuPreference = Measured<int>.Of(withoutPreference),
        };

    private static RuleResult Run(DisplaySnapshot d, DeviceKind kind = DeviceKind.Laptop) =>
        new GpuHybridRule().Evaluate(DisplayFixtures.Snapshot(d, kind), Refs);

    [Fact]
    public void HybridLaptop_WithGamesWithoutPreference_Fires()
    {
        Assert.Equal(RuleOutcome.Finding, Run(Hybrid(true, 2)).Outcome);
    }

    [Fact]
    public void AllGamesHaveAPreference_Passes()
    {
        Assert.Equal(RuleOutcome.Pass, Run(Hybrid(true, 0)).Outcome);
    }

    [Fact]
    public void Desktop_OrNoRealSwitching_DoesNotApply()
    {
        Assert.Equal(RuleOutcome.NotApplicable, Run(Hybrid(true, 2), DeviceKind.Desktop).Outcome);
        Assert.Equal(RuleOutcome.NotApplicable, Run(Hybrid(false, 2)).Outcome);
    }

    [Fact]
    public void AnotherUsersRegistry_IsNotMeasurable()
    {
        var d = Hybrid(true, 2) with
        {
            GamesWithoutGpuPreference = Measured<int>.Missing(NotMeasuredReason.AccessDenied),
        };
        Assert.Equal(RuleOutcome.NotMeasurable, Run(d).Outcome);
    }
}

public class GpuPreferenceValueTests
{
    [Theory]
    [InlineData(null, "GpuPreference=2;")]
    [InlineData("", "GpuPreference=2;")]
    [InlineData("AppStatus=0;", "AppStatus=0;GpuPreference=2;")]
    [InlineData(
        "SwapEffectUpgradeEnable=1;GpuPreference=0;",
        "SwapEffectUpgradeEnable=1;GpuPreference=2;"
    )]
    public void WithHighPerformance_KeepsTheOtherPairs(string? value, string expected)
    {
        Assert.Equal(expected, GpuPreferenceValue.WithHighPerformance(value));
    }

    [Theory]
    [InlineData("GpuPreference=1;")]
    [InlineData("GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    [InlineData("not a pair list")]
    public void APreferenceAlreadySetOrUnreadable_IsNeverTouched(string value)
    {
        Assert.True(GpuPreferenceValue.HasPreference(value));
        Assert.Null(GpuPreferenceValue.WithHighPerformance(value));
    }
}
