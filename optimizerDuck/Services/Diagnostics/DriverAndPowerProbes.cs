using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>
///     Plan of improvements F4-01 and F4-04: devices with driver problems, drivers Windows Update
///     offers, and the power plans and mode. All read-only: nothing is installed or switched.
/// </summary>
internal static class DriverAndPowerProbes
{
    // ---- Drivers (F4-01) ---------------------------------------------------------------------

    /// <summary>Present devices whose ConfigManagerErrorCode is not 0.</summary>
    public static Measured<IReadOnlyList<ProblemDevice>> ReadProblemDevices()
    {
        var devices = WmiHelper.Query(
            "SELECT Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0",
            static items =>
                items
                    .Select(mo => new ProblemDevice(
                        WmiHelper.GetString(mo, "Name") ?? "?",
                        WmiHelper.GetString(mo, "PNPClass"),
                        WmiHelper.GetInt(mo, "ConfigManagerErrorCode") ?? 0
                    ))
                    .Where(d => d.Code != 0)
                    .ToList()
        );
        return devices is null
            ? Measured<IReadOnlyList<ProblemDevice>>.Missing(NotMeasuredReason.QueryFailed)
            : Measured<IReadOnlyList<ProblemDevice>>.Of(devices);
    }

    /// <summary>
    ///     The drivers Windows Update would offer, through the Windows Update Agent (the same
    ///     search as "Check for updates"). Searching installs nothing. Fails when Windows Update
    ///     is off or there is no network: "not measurable".
    /// </summary>
    public static Measured<IReadOnlyList<DriverUpdate>> ReadWindowsUpdateDrivers()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type is null)
                return Measured<IReadOnlyList<DriverUpdate>>.Missing(NotMeasuredReason.NotReported);
            dynamic session = Activator.CreateInstance(type)!;
            session.ClientApplicationID = "Zentra";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            dynamic updates = result.Updates;
            var list = new List<DriverUpdate>();
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic update = updates.Item(i);
                string title = update.Title;
                string? driverClass = null;
                try
                {
                    driverClass = update.DriverClass;
                }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                list.Add(
                    new DriverUpdate(
                        title,
                        string.IsNullOrWhiteSpace(driverClass) ? null : driverClass
                    )
                );
            }
            return Measured<IReadOnlyList<DriverUpdate>>.Of(list);
        }
        catch (Exception ex)
            when (ex
                    is COMException
                        or UnauthorizedAccessException
                        or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            )
        {
            return Measured<IReadOnlyList<DriverUpdate>>.Missing(NotMeasuredReason.QueryFailed);
        }
    }

    // ---- Power (F4-04) -----------------------------------------------------------------------

    private static readonly Guid BalancedPlan = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static (
        Measured<IReadOnlyList<PowerPlanEntry>> Plans,
        Measured<PowerMode> Mode,
        Measured<bool> OnAc
    ) ReadPower()
    {
        var service = new PowerPlanService(NullLogger<PowerPlanService>.Instance);
        var active = service.GetActiveSchemeId();
        var zentra = Guid.TryParse(Shared.PowerPlanGUID, out var z) ? z : Guid.Empty;

        Measured<IReadOnlyList<PowerPlanEntry>> plans;
        try
        {
            plans = Measured<IReadOnlyList<PowerPlanEntry>>.Of(
                service
                    .ListSchemes()
                    .Select(s => new PowerPlanEntry(
                        s.Id,
                        s.Name,
                        s.Id == active,
                        PowerModeRule.Classify(s.Id, s.Name, zentra)
                    ))
                    .ToList()
            );
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            plans = Measured<IReadOnlyList<PowerPlanEntry>>.Missing(NotMeasuredReason.QueryFailed);
        }

        var onAc = ReadOnAc();
        // The mode only tunes the Balanced plan; with another plan it does nothing.
        var mode =
            active == BalancedPlan
                ? ReadMode(onAc)
                : Measured<PowerMode>.Missing(NotMeasuredReason.NotApplicable);
        return (plans, mode, onAc);
    }

    /// <summary>The overlay GUIDs of "Power mode" (Microsoft's power slider documentation).</summary>
    internal static Measured<PowerMode> MapOverlay(Guid overlay) =>
        overlay.ToString() switch
        {
            "961cc777-2547-4f9d-8174-7d86181b8a7a" => Measured<PowerMode>.Of(
                PowerMode.BestEfficiency
            ),
            "00000000-0000-0000-0000-000000000000" or "3af9b8d9-7c97-431d-ad78-34a8bfea439f" =>
                Measured<PowerMode>.Of(PowerMode.Balanced),
            "ded574b5-45a0-4f42-8737-46345c09c238" => Measured<PowerMode>.Of(
                PowerMode.BestPerformance
            ),
            _ => Measured<PowerMode>.Missing(NotMeasuredReason.NotReported),
        };

    private static Measured<PowerMode> ReadMode(Measured<bool> onAc)
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) == 0)
                return MapOverlay(overlay);
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

        // Older builds: the overlay Windows saved for the current power source.
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"
        );
        var name =
            onAc.TryGet(out var ac) && !ac
                ? "ActiveOverlayDcPowerScheme"
                : "ActiveOverlayAcPowerScheme";
        return key?.GetValue(name) is string text && Guid.TryParse(text, out var saved)
            ? MapOverlay(saved)
            : Measured<PowerMode>.Missing(NotMeasuredReason.NotReported);
    }

    private static Measured<bool> ReadOnAc()
    {
        if (!GetSystemPowerStatus(out var status))
            return Measured<bool>.Missing(NotMeasuredReason.QueryFailed);
        return status.ACLineStatus switch
        {
            0 => Measured<bool>.Of(false),
            1 => Measured<bool>.Of(true),
            _ => Measured<bool>.Missing(NotMeasuredReason.NotReported),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
}
