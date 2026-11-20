using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>
///     R-THERMAL: the CPU runs at 90 °C or more, or Windows logged the firmware limiting the
///     processor speed 3 times or more in 7 days. The temperature needs PawnIO; without it the
///     throttling events alone decide, and the missing input is listed.
/// </summary>
public sealed class ThermalRule : IDiagnosticRule
{
    public const double HotCelsius = 90;
    public const int ThrottlingEvents = 3;

    public string RuleId => RuleIds.Thermal;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.HardwareClean];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var thermal = snapshot.Thermal;
        var hasTemp = thermal.CpuTemperatureC.TryGet(out var celsius);
        var hasEvents = thermal.FirmwareThrottling7d.TryGet(out var events);
        if (!hasTemp && !hasEvents)
            return RuleResult.NotMeasurable(
                RuleId,
                thermal.CpuTemperatureC.Reason,
                "cpu.temperature",
                "cpu.throttling"
            );

        var evidence = new List<EvidenceFact>();
        if (hasTemp && celsius >= HotCelsius)
            evidence.Add(new("cpu.temperature", (decimal)celsius, Unit: "°C"));
        if (hasEvents && events >= ThrottlingEvents)
            evidence.Add(new("cpu.throttling", events));

        string[] missing = hasTemp ? (hasEvents ? [] : ["cpu.throttling"]) : ["cpu.temperature"];

        // A measured temperature of 90 °C or more is High. Throttling events alone are only a
        // possible heat problem (a weak charger or a firmware power limit logs them too):
        // Medium (plan F2-07, audit Z-12).
        var hot = hasTemp && celsius >= HotCelsius;
        return evidence.Count > 0
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    hot ? Severity.High : Severity.Medium,
                    evidence,
                    [ActionIds.HardwareClean]
                ),
                missing
            )
            : RuleResult.Pass(RuleId, missing);
    }
}
