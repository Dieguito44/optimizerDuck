using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Diagnostics;

/// <summary>Plan of improvements F4-01: drivers, only reported.</summary>
public class DriverRuleTests
{
    private static DiagnosticSnapshot With(
        IReadOnlyList<ProblemDevice>? devices = null,
        IReadOnlyList<DriverUpdate>? updates = null
    ) =>
        DiegoPc() with
        {
            Drivers = new DriverSnapshot
            {
                ProblemDevices = Measured<IReadOnlyList<ProblemDevice>>.Of(devices ?? []),
                WindowsUpdateDrivers = Measured<IReadOnlyList<DriverUpdate>>.Of(updates ?? []),
            },
        };

    [Fact]
    public void MissingDriver_IsMedium_AndNamesTheDevice()
    {
        var result = new DeviceProblemRule().Evaluate(
            With(devices: [new("Controlador de red", "Net", 28), new("Lector SD", null, 10)]),
            Refs
        );

        Assert.Equal(Severity.Medium, result.Finding!.Severity);
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "device.no_driver" && e.Text == "Controlador de red"
        );
        Assert.Contains(result.Finding.Evidence, e => e.Key == "device.error" && e.Number == 10);
    }

    [Theory]
    [InlineData(22)] // disabled by someone, on purpose
    [InlineData(45)] // not connected now
    public void DisabledOrUnplugged_AreNotProblems(int code)
    {
        Assert.Equal(
            RuleOutcome.Pass,
            new DeviceProblemRule()
                .Evaluate(With(devices: [new("Bluetooth", "Bluetooth", code)]), Refs)
                .Outcome
        );
    }

    [Fact]
    public void DriversOffered_AreLow_WithTheirTitles()
    {
        var result = new DriversUpdateRule().Evaluate(
            With(
                updates:
                [
                    new("Realtek - Net - 10.70", "Net"),
                    new("AMD - Display - 32.0", "Display"),
                ]
            ),
            Refs
        );
        Assert.Equal(Severity.Low, result.Finding!.Severity);
        Assert.Equal(2, result.Finding.Evidence[0].Number);
    }

    [Fact]
    public void NoWindowsUpdate_IsNotMeasurable_NeverUpToDate()
    {
        var snapshot = DiegoPc() with
        {
            Drivers = new DriverSnapshot
            {
                ProblemDevices = Measured<IReadOnlyList<ProblemDevice>>.Of([]),
                WindowsUpdateDrivers = Measured<IReadOnlyList<DriverUpdate>>.Missing(
                    NotMeasuredReason.QueryFailed
                ),
            },
        };
        Assert.Equal(
            RuleOutcome.NotMeasurable,
            new DriversUpdateRule().Evaluate(snapshot, Refs).Outcome
        );
    }
}

/// <summary>Plan of improvements F4-04: power plans and mode, only reported.</summary>
public class PowerModeRuleTests
{
    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid Zentra = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static DiagnosticSnapshot With(
        PowerMode? mode,
        bool? onAc,
        DeviceKind kind = DeviceKind.Laptop,
        params PowerPlanEntry[] plans
    )
    {
        var pc = DiegoPc();
        return pc with
        {
            Os = pc.Os with { DeviceKind = Measured<DeviceKind>.Of(kind) },
            Power = new PowerModeSnapshot
            {
                Plans = Measured<IReadOnlyList<PowerPlanEntry>>.Of(
                    plans.Length > 0
                        ? plans
                        : [new(Balanced, "Equilibrado", true, PowerPlanOrigin.Windows)]
                ),
                Mode = mode is { } m
                    ? Measured<PowerMode>.Of(m)
                    : Measured<PowerMode>.Missing(NotMeasuredReason.NotApplicable),
                OnAcPower = onAc is { } a
                    ? Measured<bool>.Of(a)
                    : Measured<bool>.Missing(NotMeasuredReason.NotReported),
            },
        };
    }

    private static RuleResult Eval(DiagnosticSnapshot s) => new PowerModeRule().Evaluate(s, Refs);

    [Fact]
    public void EfficiencyModePluggedIn_IsLow()
    {
        Assert.Equal(
            Severity.Low,
            Eval(With(PowerMode.BestEfficiency, onAc: true)).Finding!.Severity
        );
    }

    [Fact]
    public void EfficiencyModeOnADesktop_IsLow_EvenWithoutThePowerSource()
    {
        Assert.Equal(
            Severity.Low,
            Eval(With(PowerMode.BestEfficiency, onAc: null, DeviceKind.Desktop)).Finding!.Severity
        );
    }

    [Fact]
    public void EfficiencyModeOnBattery_IsFine()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(With(PowerMode.BestEfficiency, onAc: false)).Outcome);
    }

    [Fact]
    public void ManufacturerPlans_AreListedWithoutPoints()
    {
        var result = Eval(
            With(
                PowerMode.Balanced,
                onAc: true,
                DeviceKind.Laptop,
                new(Balanced, "Equilibrado", false, PowerPlanOrigin.Windows),
                new(Guid.NewGuid(), "AMD Ryzen Balanced", true, PowerPlanOrigin.Manufacturer)
            )
        );
        Assert.Equal(Severity.Info, result.Finding!.Severity);
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "power.vendor_plan_active" && e.Text == "AMD Ryzen Balanced"
        );
    }

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "Equilibrado", PowerPlanOrigin.Windows)]
    [InlineData(
        "e9a42b02-d5df-448d-aa00-03f14749eb61",
        "Máximo rendimiento",
        PowerPlanOrigin.Windows
    )]
    [InlineData("11111111-2222-3333-4444-555555555555", "optimizerDuck", PowerPlanOrigin.Zentra)]
    [InlineData(
        "9897998c-92de-4669-853f-b7cd3ecb2790",
        "AMD Ryzen Balanced",
        PowerPlanOrigin.Manufacturer
    )]
    [InlineData(
        "aaaaaaaa-0000-0000-0000-000000000001",
        "Lenovo Power Management",
        PowerPlanOrigin.Manufacturer
    )]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000002", "Mi plan", PowerPlanOrigin.Custom)]
    public void Plans_AreClassifiedByOrigin(string id, string name, PowerPlanOrigin origin)
    {
        Assert.Equal(origin, PowerModeRule.Classify(Guid.Parse(id), name, Zentra));
    }

    [Theory]
    [InlineData("961cc777-2547-4f9d-8174-7d86181b8a7a", PowerMode.BestEfficiency)]
    [InlineData("00000000-0000-0000-0000-000000000000", PowerMode.Balanced)]
    [InlineData("ded574b5-45a0-4f42-8737-46345c09c238", PowerMode.BestPerformance)]
    public void OverlayGuids_MapToTheMode(string overlay, PowerMode mode)
    {
        Assert.Equal(mode, DriverAndPowerProbes.MapOverlay(Guid.Parse(overlay)).ValueOrDefault);
    }

    [Fact]
    public void UnknownOverlay_IsNotMeasured()
    {
        Assert.False(DriverAndPowerProbes.MapOverlay(Guid.NewGuid()).HasValue);
    }
}
