using optimizerDuck.Domain.Diagnostics.Reference;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>
///     R-DEVICE-PROBLEM (plan of improvements F4-01): devices Windows reports with a problem, most
///     often code 28 (no driver installed). Disabled on purpose (22) and not connected (45) are not
///     problems. Only reported: the technician installs the driver (Medium).
/// </summary>
public sealed class DeviceProblemRule : IDiagnosticRule
{
    /// <summary>Codes that are a choice or a state, not a fault.</summary>
    internal static readonly HashSet<int> NotAProblem = [22, 45];

    public string RuleId => RuleIds.DeviceProblem;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.DeviceDriver];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var measured = snapshot.Drivers.ProblemDevices;
        if (!measured.TryGet(out var devices))
            return RuleResult.NotMeasurable(RuleId, measured.Reason, "device.problems");

        var problems = devices.Where(d => !NotAProblem.Contains(d.Code)).ToList();
        if (problems.Count == 0)
            return RuleResult.Pass(RuleId);

        var evidence = problems
            .Select(d =>
                d.Code == 28
                    ? new EvidenceFact("device.no_driver", Text: d.Name, Subject: d.DeviceClass)
                    : new EvidenceFact("device.error", d.Code, Text: d.Name, Subject: d.DeviceClass)
            )
            .ToList();
        return RuleResult.Found(
            new Finding(RuleId, Severity.Medium, evidence, [ActionIds.DeviceDriver])
        );
    }
}

/// <summary>
///     R-DRIVERS-WU (plan of improvements F4-01): Windows Update offers drivers this PC does not
///     have ("Optional updates"). Windows does not install them by itself. Low: the driver in use
///     works; the newer one may fix bugs. Zentra installs nothing from third-party sources.
/// </summary>
public sealed class DriversUpdateRule : IDiagnosticRule
{
    public string RuleId => RuleIds.DriversUpdate;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.DriverWindowsUpdate];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var measured = snapshot.Drivers.WindowsUpdateDrivers;
        if (!measured.TryGet(out var updates))
            return RuleResult.NotMeasurable(RuleId, measured.Reason, "driver.windows_update");
        if (updates.Count == 0)
            return RuleResult.Pass(RuleId);

        var evidence = new List<EvidenceFact> { new("driver.updates_available", updates.Count) };
        evidence.AddRange(
            updates.Select(u => new EvidenceFact(
                "driver.update",
                Text: u.Title,
                Subject: u.DriverClass
            ))
        );
        return RuleResult.Found(
            new Finding(RuleId, Severity.Low, evidence, [ActionIds.DriverWindowsUpdate])
        );
    }
}

/// <summary>
///     R-POWER-MODE (plan of improvements F4-04): the Windows power mode on "best power
///     efficiency" while plugged in (or on a desktop) slows the PC for no saving (Low). Plans
///     from the manufacturer (AMD, Intel, the laptop brand) are listed without points (Info): on
///     current Windows the Balanced plan is enough, but the technician should know they are there.
/// </summary>
public sealed class PowerModeRule : IDiagnosticRule
{
    public string RuleId => RuleIds.PowerModeRule;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.PowerMode];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var power = snapshot.Power;
        var hasPlans = power.Plans.TryGet(out var plans);
        var hasMode = power.Mode.TryGet(out var mode);
        if (!hasPlans && !hasMode)
            return RuleResult.NotMeasurable(
                RuleId,
                power.Plans.Reason,
                "power.plans",
                "power.mode"
            );

        var plugged =
            (power.OnAcPower.TryGet(out var ac) && ac)
            || snapshot.Os.DeviceKind
                is { HasValue: true, ValueOrDefault: Services.System.DeviceKind.Desktop };
        var vendor = hasPlans
            ? plans!.Where(p => p.Origin == PowerPlanOrigin.Manufacturer).ToList()
            : [];
        var vendorEvidence = vendor.Select(p => new EvidenceFact(
            p.IsActive ? "power.vendor_plan_active" : "power.vendor_plan",
            Text: p.Name
        ));

        if (hasMode && mode == PowerMode.BestEfficiency && plugged)
            return RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.Low,
                    [new("power.mode", Text: "best_efficiency"), .. vendorEvidence],
                    [ActionIds.PowerMode]
                )
            );

        return vendor.Count > 0
            ? RuleResult.Found(new Finding(RuleId, Severity.Info, [.. vendorEvidence], []))
            : RuleResult.Pass(RuleId);
    }

    /// <summary>
    ///     Where a plan comes from: Windows' own GUIDs, Zentra's plan, a manufacturer's (by name),
    ///     or one someone made.
    /// </summary>
    public static PowerPlanOrigin Classify(Guid id, string name, Guid zentraPlan)
    {
        if (WindowsPlans.Contains(id))
            return PowerPlanOrigin.Windows;
        if (id == zentraPlan)
            return PowerPlanOrigin.Zentra;
        return ManufacturerWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))
            ? PowerPlanOrigin.Manufacturer
            : PowerPlanOrigin.Custom;
    }

    /// <summary>Balanced, High performance, Power saver and Ultimate performance.</summary>
    private static readonly HashSet<Guid> WindowsPlans =
    [
        Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
        Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a"),
        Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"),
    ];

    private static readonly string[] ManufacturerWords =
    [
        "AMD",
        "Ryzen",
        "Intel",
        "Lenovo",
        "Dell",
        "HP ",
        "ASUS",
        "Acer",
        "MSI",
        "Samsung",
        "Huawei",
        "Gigabyte",
        "Alienware",
        "Razer",
    ];
}
