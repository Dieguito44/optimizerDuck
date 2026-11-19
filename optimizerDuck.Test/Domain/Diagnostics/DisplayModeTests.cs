using optimizerDuck.Domain.Revert.Steps;
using optimizerDuck.Services.Diagnostics;

namespace optimizerDuck.Test.Domain.Diagnostics;

public class DisplayModePlanTests
{
    private static readonly DisplayModeInfo At60 = new(1920, 1080, 32, 60, false);

    private static IReadOnlyList<MonitorHzPlan> Plan(
        RawMonitor monitor,
        DisplayModeInfo? current
    ) => DisplayModeService.Plan([monitor], _ => current);

    private static RawMonitor Hdmi(int max = 144) =>
        DisplayFixtures.DiegoPcProbe().Monitors[0] with
        {
            MaxHzAtPreferred = max,
        };

    [Fact]
    public void RaisesToTheMaximum_KeepingResolutionAndColourDepth()
    {
        var plan = Assert.Single(Plan(Hdmi(), At60));
        Assert.Equal(At60 with { Hz = 144 }, plan.Target);
    }

    [Theory]
    [InlineData(65)] // less than 10 Hz more
    [InlineData(60)] // already at the maximum
    public void SmallOrNoGain_IsLeftAlone(int max)
    {
        Assert.Empty(Plan(Hdmi(max), At60));
    }

    [Fact]
    public void TheMonitorWithoutEdid_IsNeverTouched()
    {
        var phantom = DisplayFixtures.DiegoPcProbe().Monitors[1] with
        {
            MaxHzAtPreferred = 85,
            PreferredWidth = 1280,
            PreferredHeight = 800,
        };
        Assert.Empty(Plan(phantom, new DisplayModeInfo(1280, 800, 32, 75, false)));
    }

    [Fact]
    public void NotAtNativeResolution_IsLeftAlone()
    {
        Assert.Empty(Plan(Hdmi(), new DisplayModeInfo(1280, 720, 32, 60, false)));
    }

    [Fact]
    public void WithoutAPersonToConfirm_TheDefaultAnswerIsNo()
    {
        Assert.False(new DisplayModeServiceDefaults().ConfirmAnswer);
    }

    private sealed class DisplayModeServiceDefaults
    {
        // The app replaces Confirm at startup; anywhere else (tests, no UI) nothing is kept.
        public bool ConfirmAnswer =>
            DisplayModeService.Confirm("x", TimeSpan.Zero).GetAwaiter().GetResult();
    }
}

public class DisplayModeRevertStepTests
{
    [Fact]
    public void RoundTrips_WithoutTheGdiName()
    {
        var step = new DisplayModeRevertStep
        {
            MonitorDevicePath = @"\\?\DISPLAY#ABC1234#5&1",
            MonitorName = "Monitor HDMI",
            Width = 1920,
            Height = 1080,
            Bpp = 32,
            Hz = 60,
            AppliedHz = 144,
        };

        var data = step.ToData();
        var back = DisplayModeRevertStep.FromData(data);

        Assert.Equal(step.MonitorDevicePath, back.MonitorDevicePath);
        Assert.Equal(
            (1920, 1080, 32, 60, 144),
            (back.Width, back.Height, back.Bpp, back.Hz, back.AppliedHz)
        );
        Assert.DoesNotContain(@"\\.\DISPLAY", data.ToString());
        Assert.Equal("DisplayMode", back.Type);
    }
}
