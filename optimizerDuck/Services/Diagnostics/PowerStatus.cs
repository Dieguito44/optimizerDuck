using System.Runtime.InteropServices;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>Whether the PC runs on the charger (GetSystemPowerStatus). Read-only.</summary>
public static class PowerStatus
{
    /// <summary>True on the charger, false on battery, null when Windows does not know.</summary>
    public static bool? OnAcPower()
    {
        if (!GetSystemPowerStatus(out var status))
            return null;
        return status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null,
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
}
