using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Services.System;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>
///     R-MON-HZ: a monitor runs at fewer Hz than it supports at its native resolution. Only real
///     monitors count (physical connector, EDID, not cloned); the difference must be 10 Hz or more
///     so 59/60 and 143/144 never fire; a monitor at a non-native resolution is left alone (a TV
///     at 1080p120 on purpose); an internal panel on battery is left alone (power saving).
/// </summary>
public sealed class MonitorHzRule : IDiagnosticRule
{
    public const int MinGapHz = 10;

    public string RuleId => RuleIds.MonitorHz;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.MonitorHz];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var display = snapshot.Display;
        if (display.RemoteDesktopSession is { HasValue: true, ValueOrDefault: true })
            return RuleResult.NotApplicable(RuleId);
        if (display.Monitors.Count == 0)
            return RuleResult.NotMeasurable(
                RuleId,
                NotMeasuredReason.NotReported,
                "display.monitors"
            );

        var real = display.Monitors.Where(m => m.IsPhysical && m.HasEdid && !m.IsCloned).ToList();
        if (real.Count == 0)
            return RuleResult.NotApplicable(RuleId);

        var evidence = new List<EvidenceFact>();
        var measured = 0;
        foreach (var m in real)
        {
            if (
                !m.CurrentWidth.TryGet(out var cw)
                || !m.CurrentHeight.TryGet(out var ch)
                || !m.CurrentHz.TryGet(out var hz)
                || !m.NativeWidth.TryGet(out var nw)
                || !m.NativeHeight.TryGet(out var nh)
                || !m.MaxHzAtNative.TryGet(out var max)
            )
                continue;
            measured++;

            var native = (cw == nw && ch == nh) || (cw == nh && ch == nw);
            var onBattery =
                m.Connector == DisplayConnector.Internal
                && display.OnBattery is { HasValue: true, ValueOrDefault: true };
            if (!native || onBattery || max - hz < MinGapHz)
                continue;

            var subject = m.Model.TryGet(out var model) ? model : $"#{m.Index + 1}";
            evidence.Add(new("display.current_hz", hz, Unit: "Hz", Subject: subject));
            evidence.Add(new("display.max_hz", max, Unit: "Hz", Subject: subject));
        }

        if (evidence.Count > 0)
            return RuleResult.Found(
                new Finding(RuleId, Severity.High, evidence, [ActionIds.MonitorHz])
            );
        return measured > 0
            ? RuleResult.Pass(RuleId)
            : RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, "display.max_hz");
    }
}

/// <summary>
///     R-MON-PORT: a desktop with a dedicated graphics card drives its monitors from the
///     motherboard (integrated graphics), so games run on the weaker GPU. Fires only when the
///     dedicated card can drive screens and has none, and a real external monitor is on the
///     integrated one.
/// </summary>
public sealed class MonitorPortRule : IDiagnosticRule
{
    public string RuleId => RuleIds.MonitorPort;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.MonitorCable];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Os.DeviceKind.TryGet(out var kind))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Os.DeviceKind.Reason,
                "os.device_kind"
            );
        var display = snapshot.Display;
        if (
            kind != DeviceKind.Desktop
            || display.RemoteDesktopSession is { HasValue: true, ValueOrDefault: true }
        )
            return RuleResult.NotApplicable(RuleId);
        if (display.Adapters.Any(a => !a.IsIntegrated.HasValue))
            return RuleResult.NotMeasurable(
                RuleId,
                NotMeasuredReason.NotReported,
                "gpu.integrated_only"
            );

        var dedicated = display
            .Adapters.Where(a =>
                a is { IsIntegrated.ValueOrDefault: false, IsDetachable: false }
                && a.DisplayCapable is { HasValue: true, ValueOrDefault: true }
            )
            .ToList();
        var integrated = display.Adapters.Where(a => a.IsIntegrated.ValueOrDefault).ToList();
        if (dedicated.Count == 0 || integrated.Count == 0)
            return RuleResult.NotApplicable(RuleId);
        if (display.Monitors.Any(m => m.Connector == DisplayConnector.Internal))
            return RuleResult.NotApplicable(RuleId);
        if (display.Monitors.Any(m => !m.AdapterIndex.HasValue))
            return RuleResult.NotMeasurable(
                RuleId,
                NotMeasuredReason.NotReported,
                "display.monitors"
            );

        var onIntegrated = display
            .Monitors.Where(m =>
                m.IsPhysical
                && m.HasEdid
                && integrated.Any(a => a.Index == m.AdapterIndex.ValueOrDefault)
            )
            .ToList();
        var onDedicated = display.Monitors.Count(m =>
            dedicated.Any(a => a.Index == m.AdapterIndex.ValueOrDefault)
        );

        if (onIntegrated.Count == 0 || onDedicated > 0)
            return RuleResult.Pass(RuleId);

        var evidence = new List<EvidenceFact>();
        foreach (var a in dedicated)
            evidence.Add(new("gpu.dedicated", Text: a.Model.ValueOrDefault ?? "—"));
        foreach (var a in integrated)
            evidence.Add(new("gpu.integrated", Text: a.Model.ValueOrDefault ?? "—"));
        evidence.Add(new("display.monitors_on_integrated", onIntegrated.Count));
        return RuleResult.Found(
            new Finding(RuleId, Severity.Critical, evidence, [ActionIds.MonitorCable])
        );
    }
}

/// <summary>
///     R-GPU-HYBRID: a laptop with two GPUs where Windows really switches between them, and games
///     Windows has seen run that have no GPU preference set.
/// </summary>
public sealed class GpuHybridRule : IDiagnosticRule
{
    public string RuleId => RuleIds.GpuHybrid;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.GpuPreference];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Os.DeviceKind.TryGet(out var kind))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Os.DeviceKind.Reason,
                "os.device_kind"
            );
        if (kind != DeviceKind.Laptop)
            return RuleResult.NotApplicable(RuleId);

        var display = snapshot.Display;
        var hasBoth =
            display.Adapters.Any(a => a.IsIntegrated is { HasValue: true, ValueOrDefault: true })
            && display.Adapters.Any(a =>
                a.IsIntegrated is { HasValue: true, ValueOrDefault: false }
            );
        if (!hasBoth)
            return RuleResult.NotApplicable(RuleId);

        if (!display.GpuPreferenceEffective.TryGet(out var effective))
            return RuleResult.NotMeasurable(
                RuleId,
                display.GpuPreferenceEffective.Reason,
                "gpu.preference"
            );
        if (!effective)
            return RuleResult.NotApplicable(RuleId);

        if (!display.GamesWithoutGpuPreference.TryGet(out var games))
            return RuleResult.NotMeasurable(
                RuleId,
                display.GamesWithoutGpuPreference.Reason,
                "games.no_gpu_pref"
            );

        return games > 0
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.High,
                    [new("games.no_gpu_pref", games)],
                    [ActionIds.GpuPreference]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}
