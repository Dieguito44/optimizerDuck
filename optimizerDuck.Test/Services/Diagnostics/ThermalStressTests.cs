using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Services.Diagnostics;

namespace optimizerDuck.Test.Services.Diagnostics;

/// <summary>Plan of improvements F4-02: the safety rules of the short thermal test.</summary>
public class ThermalStressTests
{
    /// <summary>A machine whose temperature, charger and sensor follow a script, second by second.</summary>
    private sealed class FakeMachine(
        bool laptop,
        Func<int, double?> temperature,
        Func<int, bool?> onAc
    ) : IStressMachine
    {
        private int _second;
        public bool Loading { get; private set; }
        public bool EverLoaded { get; private set; }
        public bool IsLaptop => laptop;

        public double? ReadCelsius() => temperature(_second);

        public bool? OnAcPower() => onAc(_second);

        public void StartLoad()
        {
            Loading = true;
            EverLoaded = true;
        }

        public void StopLoad() => Loading = false;

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _second++;
            return Task.CompletedTask;
        }

        public int? ThrottleIncidentsSince(DateTime utc) => 0;
    }

    private static Task<StressResult> Run(FakeMachine machine) =>
        RunWith(machine, TestContext.Current.CancellationToken);

    private static Task<StressResult> RunWith(FakeMachine machine, CancellationToken token) =>
        new ThermalStressTest(machine, NullLogger.Instance).RunAsync(
            TimeSpan.FromSeconds(90),
            95,
            null,
            token
        );

    [Fact]
    public void Defaults_AreThePlans()
    {
        Assert.Equal(95, ThermalStressTest.DefaultLimitC);
        Assert.InRange(ThermalStressTest.DefaultDuration.TotalSeconds, 60, 120);
    }

    [Fact]
    public async Task ACoolPc_RunsTheWholeTest_AndTheLoadStops()
    {
        var machine = new FakeMachine(false, s => 45 + Math.Min(s, 40), _ => null);
        var result = await Run(machine);

        Assert.Equal(StressOutcome.Completed, result.Outcome);
        Assert.Equal(90, result.Ran.TotalSeconds);
        Assert.Equal(85, result.MaxC);
        Assert.False(machine.Loading);
    }

    [Fact]
    public async Task ReachingTheLimit_StopsAtOnce()
    {
        var machine = new FakeMachine(false, s => s < 5 ? 50 : 50 + (s - 5) * 3, _ => null);
        var result = await Run(machine);

        Assert.Equal(StressOutcome.StoppedHot, result.Outcome);
        Assert.True(result.MaxC >= 95);
        Assert.True(result.Ran.TotalSeconds < 90);
        Assert.False(machine.Loading);
    }

    [Fact]
    public async Task ALaptopOnBattery_NeverStarts()
    {
        var machine = new FakeMachine(true, _ => 45, _ => false);
        var result = await Run(machine);
        Assert.Equal(StressOutcome.OnBattery, result.Outcome);
        Assert.False(machine.EverLoaded);
    }

    [Fact]
    public async Task UnpluggingTheCharger_StopsTheLoad()
    {
        var machine = new FakeMachine(true, _ => 60, s => s < 20);
        var result = await Run(machine);
        Assert.Equal(StressOutcome.OnBattery, result.Outcome);
        Assert.True(machine.EverLoaded);
        Assert.False(machine.Loading);
    }

    [Fact]
    public async Task NoSensor_NeverStarts_ALostSensor_Stops()
    {
        var none = new FakeMachine(false, _ => null, _ => null);
        Assert.Equal(StressOutcome.NoSensor, (await Run(none)).Outcome);
        Assert.False(none.EverLoaded);

        var lost = new FakeMachine(false, s => s < 30 ? 60 : null, _ => null);
        var result = await Run(lost);
        Assert.Equal(StressOutcome.NoSensor, result.Outcome);
        Assert.False(lost.Loading);
        Assert.True(result.Ran.TotalSeconds < 40);
    }

    [Fact]
    public async Task AlreadyAtTheLimitAtRest_NeverLoads()
    {
        var machine = new FakeMachine(false, _ => 96, _ => null);
        Assert.Equal(StressOutcome.StoppedHot, (await Run(machine)).Outcome);
        Assert.False(machine.EverLoaded);
    }

    [Fact]
    public async Task Cancelling_StopsTheLoad()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var machine = new FakeMachine(
            false,
            s =>
            {
                if (s == 20)
                    cts.Cancel();
                return 60;
            },
            _ => null
        );
        var result = await RunWith(machine, cts.Token);
        Assert.Equal(StressOutcome.Cancelled, result.Outcome);
        Assert.False(machine.Loading);
    }
}
