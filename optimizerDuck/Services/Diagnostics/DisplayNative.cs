using System.Runtime.InteropServices;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>A monitor as the display configuration (CCD) and GDI report it. Raw, unmapped.</summary>
public sealed record RawMonitor
{
    public long AdapterLuid { get; init; }
    public uint SourceId { get; init; }
    public uint OutputTechnology { get; init; }
    public bool HasEdid { get; init; }
    public string? FriendlyName { get; init; }

    /// <summary>\\.\DISPLAYn. Used only while probing; never stored in the snapshot.</summary>
    public string? GdiName { get; init; }

    public string? MonitorDevicePath { get; init; }
    public int? CurrentWidth { get; init; }
    public int? CurrentHeight { get; init; }
    public int? CurrentHz { get; init; }
    public int? CurrentBpp { get; init; }
    public int? PreferredWidth { get; init; }
    public int? PreferredHeight { get; init; }
    public int? MaxHzAtPreferred { get; init; }
}

/// <summary>A display adapter: D3DKMT type bits and sources, DXGI name and vendor.</summary>
public sealed record RawAdapter(
    long Luid,
    uint TypeBits,
    int Sources,
    string? Name,
    uint VendorId,
    bool Software
);

/// <summary>One GDI display mode.</summary>
public readonly record struct DisplayModeInfo(
    int Width,
    int Height,
    int Bpp,
    int Hz,
    bool Interlaced
);

/// <summary>
///     P/Invoke for the Windows display configuration (user32 CCD and GDI), the kernel adapter
///     types (gdi32 D3DKMT) and DXGI's GPU preference. Structure offsets were checked on Diego's
///     PC (x64, 19/11/2026). Read-only, except <see cref="ChangeMode" />.
/// </summary>
internal static class DisplayNative
{
    private const uint QdcOnlyActivePaths = 2;
    private const int PathSize = 72;
    private const int ModeSize = 64;
    private const int EnumCurrentSettings = -1;

    // DEVMODEW, display part only: the fields Zentra reads or sets.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion,
            DriverVersion,
            Size,
            DriverExtra;
        public int Fields;
        public int PositionX,
            PositionY;
        public int DisplayOrientation,
            DisplayFixedOutput;
        public short Color,
            Duplex,
            YResolution,
            TTOption,
            Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel,
            PelsWidth,
            PelsHeight,
            DisplayFlags,
            DisplayFrequency;
        public int IcmMethod,
            IcmIntent,
            MediaType,
            DitherType,
            Reserved1,
            Reserved2,
            PanningWidth,
            PanningHeight;

        public static DevMode Create() => new() { Size = (short)Marshal.SizeOf<DevMode>() };
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint paths,
        out uint modes
    );

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint paths,
        IntPtr pathArray,
        ref uint modes,
        IntPtr modeArray,
        IntPtr topology
    );

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(
        string device,
        int mode,
        ref DevMode devMode,
        int flags
    );

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string device,
        ref DevMode devMode,
        IntPtr hwnd,
        int flags,
        IntPtr param
    );

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus,
            BatteryFlag,
            BatteryLifePercent,
            SystemStatusFlag;
        public int BatteryLifeTime,
            BatteryFullLifeTime;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(IntPtr request);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(IntPtr request);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref uint handle);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapterByGpuPreferenceFn(
        IntPtr self,
        uint index,
        int preference,
        ref Guid iid,
        out IntPtr adapter
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

    public static bool IsRemoteSession() => GetSystemMetrics(0x1000) != 0; // SM_REMOTESESSION

    /// <summary>True on battery, false on AC, null when Windows does not know.</summary>
    public static bool? IsOnBattery() =>
        GetSystemPowerStatus(out var s) && s.AcLineStatus is 0 or 1 ? s.AcLineStatus == 0 : null;

    /// <summary>The active monitors. Throws <see cref="InvalidOperationException" /> when CCD fails.</summary>
    public static List<RawMonitor> ReadMonitors()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var nPaths, out var nModes) != 0)
            throw new InvalidOperationException("GetDisplayConfigBufferSizes failed");
        var paths = Marshal.AllocHGlobal((int)nPaths * PathSize);
        var modes = Marshal.AllocHGlobal((int)nModes * ModeSize);
        try
        {
            if (
                QueryDisplayConfig(
                    QdcOnlyActivePaths,
                    ref nPaths,
                    paths,
                    ref nModes,
                    modes,
                    IntPtr.Zero
                ) != 0
            )
                throw new InvalidOperationException("QueryDisplayConfig failed");

            var monitors = new List<RawMonitor>();
            for (var i = 0; i < nPaths; i++)
            {
                var p = IntPtr.Add(paths, i * PathSize);
                var sourceLuid = Marshal.ReadInt64(p, 0);
                var sourceId = (uint)Marshal.ReadInt32(p, 8);
                var targetLuid = Marshal.ReadInt64(p, 20);
                var targetId = (uint)Marshal.ReadInt32(p, 28);
                var tech = (uint)Marshal.ReadInt32(p, 36);

                var gdiName = ReadString(DeviceInfo(1, 84, sourceLuid, sourceId), 20);
                using var target = DeviceInfo(2, 420, targetLuid, targetId);
                var flags = target.Ok ? Marshal.ReadInt32(target.Ptr, 20) : 0;
                var hasEdid = (flags & 0x4) != 0; // edidIdsValid
                string? friendly = target.Ok
                    ? Marshal.PtrToStringUni(IntPtr.Add(target.Ptr, 36))
                    : null;
                string? devicePath = target.Ok
                    ? Marshal.PtrToStringUni(IntPtr.Add(target.Ptr, 164))
                    : null;

                int? prefW = null,
                    prefH = null;
                if (hasEdid)
                {
                    using var pref = DeviceInfo(3, 80, targetLuid, targetId);
                    if (pref.Ok)
                    {
                        prefW = Marshal.ReadInt32(pref.Ptr, 20);
                        prefH = Marshal.ReadInt32(pref.Ptr, 24);
                    }
                }

                DisplayModeInfo? current = null;
                int? maxHz = null;
                if (gdiName is not null)
                {
                    current = CurrentMode(gdiName);
                    if (current is { } c && prefW is { } w && prefH is { } h)
                        maxHz = Modes(gdiName)
                            .Where(m =>
                                !m.Interlaced
                                && m.Bpp == c.Bpp
                                && (
                                    (m.Width == w && m.Height == h)
                                    || (m.Width == h && m.Height == w)
                                )
                            )
                            .Select(m => (int?)m.Hz)
                            .Max();
                }

                monitors.Add(
                    new RawMonitor
                    {
                        AdapterLuid = targetLuid,
                        SourceId = sourceId,
                        OutputTechnology = tech,
                        HasEdid = hasEdid,
                        FriendlyName = string.IsNullOrWhiteSpace(friendly) ? null : friendly,
                        GdiName = gdiName,
                        MonitorDevicePath = devicePath,
                        CurrentWidth = current?.Width,
                        CurrentHeight = current?.Height,
                        CurrentHz = current?.Hz is > 1 and var hz ? hz : null,
                        CurrentBpp = current?.Bpp,
                        PreferredWidth = prefW,
                        PreferredHeight = prefH,
                        MaxHzAtPreferred = maxHz,
                    }
                );
            }
            return monitors;
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }

    public static DisplayModeInfo? CurrentMode(string gdiName)
    {
        var dm = DevMode.Create();
        return EnumDisplaySettingsEx(gdiName, EnumCurrentSettings, ref dm, 0) ? ToInfo(dm) : null;
    }

    public static IEnumerable<DisplayModeInfo> Modes(string gdiName)
    {
        for (var i = 0; ; i++)
        {
            var dm = DevMode.Create();
            if (!EnumDisplaySettingsEx(gdiName, i, ref dm, 0))
                yield break;
            yield return ToInfo(dm);
        }
    }

    /// <summary>
    ///     ChangeDisplaySettingsEx with only resolution, colour depth and refresh. flags: 2 test,
    ///     0 dynamic (not saved), 1 save to the registry. Returns DISP_CHANGE_* (0 = success).
    /// </summary>
    public static int ChangeMode(string gdiName, DisplayModeInfo mode, int flags)
    {
        var dm = DevMode.Create();
        dm.PelsWidth = mode.Width;
        dm.PelsHeight = mode.Height;
        dm.BitsPerPel = mode.Bpp;
        dm.DisplayFrequency = mode.Hz;
        dm.Fields = 0x80000 | 0x100000 | 0x40000 | 0x400000; // width, height, bpp, frequency
        return ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
    }

    /// <summary>Hardware and software adapters from the kernel, with DXGI names joined by LUID.</summary>
    public static List<RawAdapter> ReadAdapters()
    {
        var names = DxgiAdapters(0).ToDictionary(a => a.Luid);
        var adapters = new List<RawAdapter>();
        var request = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(request, 0, 0);
            Marshal.WriteInt64(request, 8, 0);
            if (D3DKMTEnumAdapters2(request) != 0)
                throw new InvalidOperationException("D3DKMTEnumAdapters2 failed");
            var count = Marshal.ReadInt32(request, 0);
            var array = Marshal.AllocHGlobal(Math.Max(1, count) * 20);
            try
            {
                Marshal.WriteInt64(request, 8, array.ToInt64());
                if (D3DKMTEnumAdapters2(request) != 0)
                    throw new InvalidOperationException("D3DKMTEnumAdapters2 failed");
                count = Marshal.ReadInt32(request, 0);
                for (var i = 0; i < count; i++)
                {
                    var a = IntPtr.Add(array, i * 20);
                    var handle = (uint)Marshal.ReadInt32(a, 0);
                    var luid = Marshal.ReadInt64(a, 4);
                    var sources = Marshal.ReadInt32(a, 12);
                    var type = QueryAdapterType(handle);
                    D3DKMTCloseAdapter(ref handle);
                    names.TryGetValue(luid, out var dxgi);
                    adapters.Add(
                        new RawAdapter(
                            luid,
                            type,
                            sources,
                            dxgi?.Name,
                            dxgi?.VendorId ?? 0,
                            dxgi?.Software ?? false
                        )
                    );
                }
            }
            finally
            {
                Marshal.FreeHGlobal(array);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(request);
        }
        return adapters;
    }

    /// <summary>
    ///     Whether "power saving" (1) and "high performance" (2) pick different hardware adapters,
    ///     which is what makes a per-app GPU preference do anything. Null when DXGI cannot say.
    /// </summary>
    public static bool? GpuPreferenceIsEffective()
    {
        var saving = DxgiAdapters(1).FirstOrDefault(a => !a.Software);
        var performance = DxgiAdapters(2).FirstOrDefault(a => !a.Software);
        return saving is null || performance is null ? null : saving.Luid != performance.Luid;
    }

    private sealed record DxgiAdapter(long Luid, string? Name, uint VendorId, bool Software);

    /// <summary>Adapters in the order IDXGIFactory6 gives for a preference (0 = unspecified).</summary>
    private static List<DxgiAdapter> DxgiAdapters(int preference)
    {
        var result = new List<DxgiAdapter>();
        var factoryIid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref factoryIid, out var factory) != 0)
            return result;
        try
        {
            var factory6Iid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
            if (Marshal.QueryInterface(factory, in factory6Iid, out var factory6) != 0)
                return result;
            try
            {
                var enumFn = Marshal.GetDelegateForFunctionPointer<EnumAdapterByGpuPreferenceFn>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory6), 29 * IntPtr.Size)
                );
                var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
                var desc = Marshal.AllocHGlobal(312);
                try
                {
                    for (
                        uint i = 0;
                        enumFn(factory6, i, preference, ref adapterIid, out var adapter) == 0;
                        i++
                    )
                    {
                        try
                        {
                            var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Fn>(
                                Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size)
                            );
                            if (getDesc(adapter, desc) != 0)
                                continue;
                            result.Add(
                                new DxgiAdapter(
                                    Marshal.ReadInt64(desc, 296),
                                    Marshal.PtrToStringUni(desc),
                                    (uint)Marshal.ReadInt32(desc, 256),
                                    (Marshal.ReadInt32(desc, 304) & 2) != 0 // DXGI_ADAPTER_FLAG_SOFTWARE
                                )
                            );
                        }
                        finally
                        {
                            Marshal.Release(adapter);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(desc);
                }
            }
            finally
            {
                Marshal.Release(factory6);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
        return result;
    }

    private static uint QueryAdapterType(uint handle)
    {
        var data = Marshal.AllocHGlobal(4);
        var query = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(data, 0, 0);
            Marshal.WriteInt32(query, 0, (int)handle);
            Marshal.WriteInt32(query, 4, 15); // KMTQAITYPE_ADAPTERTYPE
            Marshal.WriteInt64(query, 8, data.ToInt64());
            Marshal.WriteInt32(query, 16, 4);
            Marshal.WriteInt32(query, 20, 0);
            return D3DKMTQueryAdapterInfo(query) == 0 ? (uint)Marshal.ReadInt32(data, 0) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(query);
            Marshal.FreeHGlobal(data);
        }
    }

    private static DisplayModeInfo ToInfo(DevMode dm) =>
        new(
            dm.PelsWidth,
            dm.PelsHeight,
            dm.BitsPerPel,
            dm.DisplayFrequency,
            (dm.DisplayFlags & 2) != 0
        );

    private sealed class Packet(IntPtr ptr, bool ok) : IDisposable
    {
        public IntPtr Ptr { get; } = ptr;
        public bool Ok { get; } = ok;

        public void Dispose() => Marshal.FreeHGlobal(Ptr);
    }

    /// <summary>A DISPLAYCONFIG_DEVICE_INFO request: header type, size, adapter LUID and id.</summary>
    private static Packet DeviceInfo(int type, int size, long luid, uint id)
    {
        var p = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++)
            Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, type);
        Marshal.WriteInt32(p, 4, size);
        Marshal.WriteInt64(p, 8, luid);
        Marshal.WriteInt32(p, 16, (int)id);
        return new Packet(p, DisplayConfigGetDeviceInfo(p) == 0);
    }

    private static string? ReadString(Packet packet, int offset)
    {
        using (packet)
            return packet.Ok ? Marshal.PtrToStringUni(IntPtr.Add(packet.Ptr, offset)) : null;
    }
}
