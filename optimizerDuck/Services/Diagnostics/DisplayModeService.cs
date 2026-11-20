using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Revert.Steps;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>A monitor that ACT-MON-HZ can raise, and to what.</summary>
public sealed record MonitorHzPlan(
    RawMonitor Monitor,
    DisplayModeInfo Current,
    DisplayModeInfo Target
);

/// <summary>
///     ACT-MON-HZ (plan phase 6B, designed with the Fable consult). The technician works through
///     RustDesk, which captures before the video output: if the cable cannot carry the new mode,
///     the client sees black and the technician sees nothing wrong. So every change is tested,
///     applied without saving, verified, and kept only if someone at the PC confirms within 15
///     seconds; otherwise it goes back by itself. Only then is it saved.
/// </summary>
public static class DisplayModeService
{
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Asks the person at the PC whether they see the picture. Set by the app to a window with
    ///     a countdown; the default answers "no", so nothing is ever kept without a person.
    /// </summary>
    public static Func<string, TimeSpan, Task<bool>> Confirm { get; set; } =
        (_, _) => Task.FromResult(false);

    /// <summary>The monitors worth raising, with the same conditions as R-MON-HZ. Pure.</summary>
    public static IReadOnlyList<MonitorHzPlan> Plan(
        IEnumerable<RawMonitor> monitors,
        Func<string, DisplayModeInfo?> currentMode
    )
    {
        var list = monitors.ToList();
        var plans = new List<MonitorHzPlan>();
        foreach (var m in list)
        {
            var connector = DisplaySnapshotMapper.MapConnector(m.OutputTechnology);
            var cloned =
                list.Count(o => o.AdapterLuid == m.AdapterLuid && o.SourceId == m.SourceId) > 1;
            if (
                m.GdiName is not { } gdi
                || !m.HasEdid
                || cloned
                || !new MonitorSnapshot { Connector = connector }.IsPhysical
                || m.PreferredWidth is not { } w
                || m.PreferredHeight is not { } h
                || m.MaxHzAtPreferred is not { } max
                || currentMode(gdi) is not { } current
            )
                continue;
            var native =
                (current.Width == w && current.Height == h)
                || (current.Width == h && current.Height == w);
            if (native && max - current.Hz >= MonitorHzRule.MinGapHz)
                plans.Add(new MonitorHzPlan(m, current, current with { Hz = max }));
        }
        return plans;
    }

    /// <summary>The dry run (plan F2-03): each monitor's rate now and after. Changes nothing.</summary>
    public static void Preview(OpCall call)
    {
        if (TryReadMonitors(call) is not { } monitors)
            return;
        foreach (var plan in Plan(monitors, DisplayNative.CurrentMode))
            call.Preview?.Add(
                PlannedChangeKind.Command,
                plan.Monitor.FriendlyName ?? plan.Monitor.GdiName ?? "monitor",
                $"{plan.Current.Hz} Hz",
                $"{plan.Target.Hz} Hz"
            );
    }

    /// <summary>
    ///     The monitors, or null when Windows does not give the display configuration: no screen
    ///     attached to the session (a remote or headless session, the monitor off). Found in the
    ///     Hyper-V test, 20/11/2026: it threw and stopped the whole profile preview.
    /// </summary>
    private static List<RawMonitor>? TryReadMonitors(OpCall call)
    {
        try
        {
            return DisplayNative.ReadMonitors();
        }
        catch (InvalidOperationException ex)
        {
            call.Logger.LogWarning("[DISPLAY] Monitors not readable: {Error}", ex.Message);
            return null;
        }
    }

    public static async Task ApplyAsync(OpCall call)
    {
        if (TryReadMonitors(call) is not { } monitors)
        {
            call.Changes.AddNotApplicable(
                ServiceStrings.DisplayName,
                ServiceStrings.DisplayNotReadable
            );
            return;
        }
        var plans = Plan(monitors, DisplayNative.CurrentMode);
        if (plans.Count == 0)
        {
            call.Changes.AddSkip(ServiceStrings.DisplayName, ServiceStrings.DisplayAlreadyAtMax);
            return;
        }

        foreach (var plan in plans)
            await ApplyOneAsync(call, plan);
    }

    private static async Task ApplyOneAsync(OpCall call, MonitorHzPlan plan)
    {
        var gdi = plan.Monitor.GdiName!;
        var name = plan.Monitor.FriendlyName ?? gdi;
        var description = $"{name}: {plan.Current.Hz} Hz -> {plan.Target.Hz} Hz";

        // 1. Ask the driver whether the mode can be set at all.
        if (DisplayNative.ChangeMode(gdi, plan.Target, 2) != 0) // CDS_TEST
        {
            call.Changes.Add(
                ServiceStrings.DisplayName,
                description,
                false,
                error: ServiceStrings.DisplayErrorNotSupported
            );
            return;
        }

        // 2. Apply without saving, 3. check that Windows really runs it.
        var applied = DisplayNative.ChangeMode(gdi, plan.Target, 0);
        await Task.Delay(TimeSpan.FromSeconds(2), call.CancellationToken);
        if (applied != 0 || DisplayNative.CurrentMode(gdi)?.Hz != plan.Target.Hz)
        {
            DisplayNative.ChangeMode(gdi, plan.Current, 0);
            call.Changes.Add(
                ServiceStrings.DisplayName,
                description,
                false,
                error: ServiceStrings.DisplayErrorNotApplied
            );
            return;
        }

        // 4. Someone at the PC must confirm they see the picture; silence means "go back".
        var confirmed = await Confirm(name, ConfirmTimeout);
        if (!confirmed)
        {
            DisplayNative.ChangeMode(gdi, plan.Current, 0);
            call.Logger.LogInformation(
                "[DISPLAY] {Monitor}: not confirmed, back to {Hz} Hz",
                name,
                plan.Current.Hz
            );
            call.Changes.AddRefused(
                ServiceStrings.DisplayName,
                $"{description} ({ServiceStrings.DisplayNotConfirmed})"
            );
            return;
        }

        // 5. Only now save it, with the way back recorded.
        if (DisplayNative.ChangeMode(gdi, plan.Target, 1) != 0) // CDS_UPDATEREGISTRY
        {
            DisplayNative.ChangeMode(gdi, plan.Current, 0);
            call.Changes.Add(
                ServiceStrings.DisplayName,
                description,
                false,
                error: ServiceStrings.DisplayErrorNotApplied
            );
            return;
        }

        var revert = new DisplayModeRevertStep
        {
            MonitorDevicePath = plan.Monitor.MonitorDevicePath ?? string.Empty,
            MonitorName = name,
            Width = plan.Current.Width,
            Height = plan.Current.Height,
            Bpp = plan.Current.Bpp,
            Hz = plan.Current.Hz,
            AppliedHz = plan.Target.Hz,
        };
        call.Logger.LogInformation("[DISPLAY] {Description} confirmed and saved", description);
        call.Changes.Add(ServiceStrings.DisplayName, description, true, revert);
    }

    /// <summary>Puts a monitor back to its previous mode. See <see cref="DisplayModeRevertStep" />.</summary>
    internal static bool Restore(DisplayModeRevertStep step, ILogger logger)
    {
        var monitor = DisplayNative
            .ReadMonitors()
            .FirstOrDefault(m =>
                string.Equals(
                    m.MonitorDevicePath,
                    step.MonitorDevicePath,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        if (monitor?.GdiName is not { } gdi)
            throw new Domain.Exceptions.StepExecutionException(
                ServiceStrings.DisplayErrorMonitorMissing,
                step.MonitorName
            );

        if (DisplayNative.CurrentMode(gdi)?.Hz != step.AppliedHz)
        {
            // Someone changed the refresh rate after Zentra: leave their choice alone.
            logger.LogInformation(
                "[DISPLAY][REVERT] {Monitor} no longer at {Hz} Hz: left as it is",
                step.MonitorName,
                step.AppliedHz
            );
            return true;
        }

        var previous = new DisplayModeInfo(step.Width, step.Height, step.Bpp, step.Hz, false);
        return DisplayNative.ChangeMode(gdi, previous, 1) == 0;
    }
}
