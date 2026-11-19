using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>What the display probe read, before mapping. Holds device names: never stored.</summary>
public sealed record DisplayProbeResult(
    IReadOnlyList<RawAdapter> Adapters,
    IReadOnlyList<RawMonitor> Monitors,
    bool? RemoteSession,
    bool? OnBattery,
    bool? PreferenceEffective,
    Measured<int> Games,
    Measured<int> GamesWithoutPreference,
    SectionStatus Status
)
{
    public static DisplayProbeResult Failed(SectionStatus status, NotMeasuredReason reason) =>
        new(
            [],
            [],
            null,
            null,
            null,
            Measured<int>.Missing(reason),
            Measured<int>.Missing(reason),
            status
        );
}

/// <summary>Reads monitors, adapters and games (phase 6B). Read-only.</summary>
public static class DisplayProbe
{
    public static DisplayProbeResult Read()
    {
        List<RawAdapter> adapters;
        List<RawMonitor> monitors;
        try
        {
            adapters = DisplayNative.ReadAdapters();
            monitors = DisplayNative.ReadMonitors();
        }
        catch (Exception ex)
            when (ex
                    is InvalidOperationException
                        or DllNotFoundException
                        or EntryPointNotFoundException
                        or COMException
            )
        {
            return DisplayProbeResult.Failed(SectionStatus.Failed, NotMeasuredReason.QueryFailed);
        }

        var (games, withoutPreference) = CountGames();
        return new DisplayProbeResult(
            adapters,
            monitors,
            DisplayNative.IsRemoteSession(),
            DisplayNative.IsOnBattery(),
            DisplayNative.GpuPreferenceIsEffective(),
            games,
            withoutPreference,
            SectionStatus.Ok
        );
    }

    /// <summary>
    ///     Games Windows has seen run, and how many have no GPU preference. HKCU must be the
    ///     signed-in user's: when Zentra was elevated with another account it is not, and the
    ///     count is not measurable rather than wrong.
    /// </summary>
    private static (Measured<int>, Measured<int>) CountGames()
    {
        if (!ConsoleUser.IsCurrentProcessUser())
            return (
                Measured<int>.Missing(NotMeasuredReason.AccessDenied),
                Measured<int>.Missing(NotMeasuredReason.AccessDenied)
            );
        try
        {
            var games = GameExecutableLocator.Find();
            var without = games.Count(g =>
                !GpuPreferenceValue.HasPreference(GpuPreferenceValue.Read(g))
            );
            return (Measured<int>.Of(games.Count), Measured<int>.Of(without));
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or global::System.Security.SecurityException
            )
        {
            return (
                Measured<int>.Missing(NotMeasuredReason.QueryFailed),
                Measured<int>.Missing(NotMeasuredReason.QueryFailed)
            );
        }
    }
}

/// <summary>Turns a <see cref="DisplayProbeResult" /> into the anonymous snapshot section. Pure.</summary>
public static class DisplaySnapshotMapper
{
    private const uint Render = 1 << 0,
        Display = 1 << 1,
        Software = 1 << 2,
        HybridDiscrete = 1 << 4,
        HybridIntegrated = 1 << 5,
        Indirect = 1 << 6,
        Detachable = 1 << 10,
        ComputeOnly = 1 << 11;

    public static DisplaySnapshot Map(DisplayProbeResult raw, CpuVendor cpuVendor, string? cpuName)
    {
        if (raw.Status != SectionStatus.Ok)
            return new DisplaySnapshot
            {
                GamesDetected = raw.Games,
                GamesWithoutGpuPreference = raw.GamesWithoutPreference,
            };

        var hardware = raw
            .Adapters.Where(a =>
                (a.TypeBits & (Render | Display)) != 0
                && (a.TypeBits & (Software | Indirect)) == 0
                && !a.Software
            )
            .ToList();

        var adapters = hardware
            .Select(
                (a, i) =>
                    new DisplayAdapterSnapshot
                    {
                        Index = i,
                        Model = Text(a.Name),
                        IsIntegrated = IsIntegrated(a, cpuVendor, cpuName) is { } integrated
                            ? Measured<bool>.Of(integrated)
                            : Measured<bool>.Missing(NotMeasuredReason.NotReported),
                        DisplayCapable = Measured<bool>.Of(
                            (a.TypeBits & Display) != 0
                                && (a.TypeBits & ComputeOnly) == 0
                                && a.Sources > 0
                        ),
                        IsDetachable = (a.TypeBits & Detachable) != 0,
                    }
            )
            .ToList();

        var monitors = raw
            .Monitors.Select(
                (m, i) =>
                {
                    var adapterIndex = hardware.FindIndex(a => a.Luid == m.AdapterLuid);
                    var connector = MapConnector(m.OutputTechnology);
                    var physical = new MonitorSnapshot { Connector = connector }.IsPhysical;
                    return new MonitorSnapshot
                    {
                        Index = i,
                        Model = Text(m.FriendlyName),
                        Connector = connector,
                        HasEdid = m.HasEdid,
                        IsCloned =
                            raw.Monitors.Count(o =>
                                o.AdapterLuid == m.AdapterLuid && o.SourceId == m.SourceId
                            ) > 1,
                        AdapterIndex =
                            adapterIndex >= 0
                                ? Measured<int>.Of(adapterIndex)
                                : Measured<int>.Missing(NotMeasuredReason.NotReported),
                        CurrentWidth = Int(m.CurrentWidth),
                        CurrentHeight = Int(m.CurrentHeight),
                        CurrentHz = Int(m.CurrentHz),
                        NativeWidth = Int(m.PreferredWidth),
                        NativeHeight = Int(m.PreferredHeight),
                        MaxHzAtNative = !physical
                            ? Measured<int>.Missing(NotMeasuredReason.NotApplicable)
                            : Int(m.MaxHzAtPreferred),
                    };
                }
            )
            .ToList();

        return new DisplaySnapshot
        {
            Adapters = adapters,
            Monitors = monitors,
            RemoteDesktopSession = Bool(raw.RemoteSession),
            OnBattery = Bool(raw.OnBattery),
            GpuPreferenceEffective = Bool(raw.PreferenceEffective),
            GamesDetected = raw.Games,
            GamesWithoutGpuPreference = raw.GamesWithoutPreference,
        };
    }

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY grouped by what matters for the rules.</summary>
    public static DisplayConnector MapConnector(uint technology) =>
        technology switch
        {
            0 => DisplayConnector.Vga,
            4 => DisplayConnector.Dvi,
            5 => DisplayConnector.Hdmi,
            10 or 18 => DisplayConnector.DisplayPort, // external, USB-C tunnel
            6 or 11 or 13 or 0x80000000 => DisplayConnector.Internal, // LVDS, eDP, UDI embedded
            15 => DisplayConnector.Wireless, // Miracast
            16 => DisplayConnector.IndirectWired, // DisplayLink and similar
            17 => DisplayConnector.Virtual, // remote tools, virtual displays
            _ => DisplayConnector.Other,
        };

    /// <summary>The kernel's hybrid bits first; the name classifier only when they say nothing.</summary>
    public static bool? IsIntegrated(RawAdapter a, CpuVendor cpuVendor, string? cpuName)
    {
        if ((a.TypeBits & HybridDiscrete) != 0)
            return false;
        if ((a.TypeBits & HybridIntegrated) != 0)
            return true;
        var vendor = a.VendorId switch
        {
            0x10DE => GpuVendor.Nvidia,
            0x1002 or 0x1022 => GpuVendor.Amd,
            0x8086 => GpuVendor.Intel,
            _ => GpuVendor.Unknown,
        };
        return IntegratedGpuClassifier.Classify(cpuVendor, cpuName, vendor, a.Name);
    }

    private static Measured<string> Text(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t)
            ? Measured<string>.Missing(NotMeasuredReason.NotReported)
            : Measured<string>.Of(t.Length > 64 ? t[..64] : t);
    }

    private static Measured<int> Int(int? v) =>
        v is { } x and > 0
            ? Measured<int>.Of(x)
            : Measured<int>.Missing(NotMeasuredReason.NotReported);

    private static Measured<bool> Bool(bool? v) =>
        v is { } x ? Measured<bool>.Of(x) : Measured<bool>.Missing(NotMeasuredReason.NotReported);
}

/// <summary>
///     The value DirectX keeps per game under HKCU\Software\Microsoft\DirectX\UserGpuPreferences:
///     "Key=Value;" pairs (GpuPreference, SwapEffectUpgradeEnable, AppStatus...). Zentra only
///     adds or sets GpuPreference and keeps every other pair as it was.
/// </summary>
public static class GpuPreferenceValue
{
    public const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>The pairs, in order, or null when the text is not "Key=Value;" pairs.</summary>
    public static List<KeyValuePair<string, string>>? Parse(string? value)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (
            var part in (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
        )
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                return null;
            pairs.Add(new(part[..eq].Trim(), part[(eq + 1)..].Trim()));
        }
        return pairs;
    }

    /// <summary>Has a GPU preference other than "let Windows decide" (0). Unreadable counts as set.</summary>
    public static bool HasPreference(string? value)
    {
        var pairs = Parse(value);
        if (pairs is null)
            return true;
        var pref = pairs.FirstOrDefault(p =>
            p.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)
        );
        return pref.Key is not null && pref.Value != "0";
    }

    /// <summary>
    ///     The value with GpuPreference=2 (high performance), keeping the other pairs; null when it
    ///     must not be touched (already has a preference, or unreadable).
    /// </summary>
    public static string? WithHighPerformance(string? value)
    {
        if (HasPreference(value))
            return null;
        var pairs = Parse(value)!;
        var index = pairs.FindIndex(p =>
            p.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)
        );
        if (index >= 0)
            pairs[index] = new("GpuPreference", "2");
        else
            pairs.Add(new("GpuPreference", "2"));
        return string.Concat(pairs.Select(p => $"{p.Key}={p.Value};"));
    }

    public static string? Read(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(exePath) as string;
    }
}

/// <summary>
///     Game executables Windows or a launcher knows about: the Game Bar's record of games it saw
///     run (GameConfigStore) and Epic Games manifests. Steam is not read: its manifests do not
///     name the executable. Only existing files, no Store apps.
/// </summary>
public static class GameExecutableLocator
{
    public static IReadOnlyList<string> Find()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children"))
            if (children is not null)
                foreach (var name in children.GetSubKeyNames())
                {
                    using var child = children.OpenSubKey(name);
                    if (child?.GetValue("MatchedExeFullPath") is string exe)
                        Add(found, exe);
                }

        var epic = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests"
        );
        if (Directory.Exists(epic))
            foreach (var item in Directory.EnumerateFiles(epic, "*.item"))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(item));
                    var dir = json.Value<string>("InstallLocation");
                    var exe = json.Value<string>("LaunchExecutable");
                    if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(exe))
                        Add(found, Path.Combine(dir, exe));
                }
                catch (Exception ex)
                    when (ex is IOException or Newtonsoft.Json.JsonException or ArgumentException)
                { }
            }

        return found.ToList();
    }

    private static void Add(HashSet<string> set, string path)
    {
        if (
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path)
        )
            set.Add(Path.GetFullPath(path));
    }
}

/// <summary>Whether Zentra runs as the user signed in at the console (so HKCU is theirs).</summary>
internal static class ConsoleUser
{
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        uint session,
        int infoClass,
        out IntPtr buffer,
        out uint bytes
    );

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    public static bool IsCurrentProcessUser()
    {
        var session = WTSGetActiveConsoleSessionId();
        var user = Query(session, 5); // WTSUserName
        var domain = Query(session, 7); // WTSDomainName
        if (string.IsNullOrEmpty(user))
            return true; // no console user (remote-only machine): nothing to compare against
        var current = WindowsIdentity.GetCurrent().Name; // DOMAIN\user
        return current.Equals($"{domain}\\{user}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The SID of the user signed in at the console, or null when there is none.</summary>
    public static string? ConsoleUserSid()
    {
        var session = WTSGetActiveConsoleSessionId();
        var user = Query(session, 5);
        var domain = Query(session, 7);
        if (string.IsNullOrEmpty(user))
            return null;
        var account = string.IsNullOrEmpty(domain)
            ? new NTAccount(user)
            : new NTAccount(domain, user);
        return account.Translate(typeof(SecurityIdentifier)).Value;
    }

    private static string? Query(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _))
            return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}
