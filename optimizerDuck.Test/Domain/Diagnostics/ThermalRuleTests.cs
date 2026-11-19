using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Domain.Diagnostics;

public class ThermalRuleTests
{
    private static RuleResult Run(Measured<double> celsius, Measured<int> throttling) =>
        new ThermalRule().Evaluate(
            DiegoPc() with
            {
                Thermal = new ThermalSnapshot
                {
                    CpuTemperatureC = celsius,
                    FirmwareThrottling7d = throttling,
                },
            },
            Refs
        );

    [Theory]
    [InlineData(45.0, 0, RuleOutcome.Pass)]
    [InlineData(90.0, 0, RuleOutcome.Finding)]
    [InlineData(60.0, 3, RuleOutcome.Finding)]
    [InlineData(60.0, 2, RuleOutcome.Pass)]
    public void HotOrThrottled_Fires(double celsius, int events, RuleOutcome expected)
    {
        Assert.Equal(expected, Run(Measured<double>.Of(celsius), Measured<int>.Of(events)).Outcome);
    }

    [Theory]
    [InlineData(92.0, 0, Severity.High)] // measured heat
    [InlineData(60.0, 4, Severity.Medium)] // throttling events alone: possible heat
    public void OnlyAMeasuredTemperatureIsHigh(double celsius, int events, Severity expected)
    {
        var result = Run(Measured<double>.Of(celsius), Measured<int>.Of(events));
        Assert.Equal(expected, result.Finding!.Severity);
    }

    [Fact]
    public void ThrottlingEventsInTheSameMinuteAreOneIncident()
    {
        var t = new DateTime(2026, 11, 19, 10, 30, 5);
        var oneIncidentOn12Threads = Enumerable.Range(0, 12).Select(i => t.AddMilliseconds(i * 10));
        Assert.Equal(
            1,
            optimizerDuck.Services.Diagnostics.ThermalService.CountIncidents(oneIncidentOn12Threads)
        );

        DateTime[] threeIncidents = [t, t.AddMinutes(5), t.AddDays(1)];
        Assert.Equal(
            3,
            optimizerDuck.Services.Diagnostics.ThermalService.CountIncidents(threeIncidents)
        );
    }

    [Fact]
    public void WithoutPawnIo_TheEventsDecide_AndTheTemperatureIsListedAsMissing()
    {
        var result = Run(
            Measured<double>.Missing(NotMeasuredReason.RequiresExternalTool),
            Measured<int>.Of(0)
        );
        Assert.Equal(RuleOutcome.Pass, result.Outcome);
        Assert.Contains("cpu.temperature", result.MissingInputs);
    }

    [Fact]
    public void NothingMeasured_IsNotMeasurable()
    {
        var result = Run(
            Measured<double>.Missing(NotMeasuredReason.RequiresExternalTool),
            Measured<int>.Missing(NotMeasuredReason.QueryFailed)
        );
        Assert.Equal(RuleOutcome.NotMeasurable, result.Outcome);
    }
}
