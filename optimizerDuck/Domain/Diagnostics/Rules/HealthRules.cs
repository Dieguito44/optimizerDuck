using System.Text.RegularExpressions;
using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Services.System;

namespace optimizerDuck.Domain.Diagnostics.Rules;

// Extended diagnosis (version 1.2.0). Ideas from the NexCore analysis, rewritten from scratch:
// CPU at rest, missing runtimes, program crashes, clock, dumps, SSD maker tools, antivirus and
// leaks. All read-only; the actions are steps the technician takes by hand.

/// <summary>R-CPU-IDLE-HIGH: the CPU stays busy with nothing open (session measurement only).</summary>
public sealed class CpuIdleHighRule : IDiagnosticRule
{
    internal const double MaxPercent = 10;
    private const double HighPercent = 25;

    public string RuleId => RuleIds.CpuIdleHigh;
    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.HeavyApps, ActionIds.StartupReview];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var idle = snapshot.Health.IdleCpuPercent;
        if (!idle.TryGet(out var percent))
            return RuleResult.NotMeasurable(RuleId, idle.Reason, "cpu.idle_percent");
        if (percent <= MaxPercent)
            return RuleResult.Pass(RuleId);
        return RuleResult.Found(
            new Finding(
                RuleId,
                percent > HighPercent ? Severity.High : Severity.Medium,
                [new("cpu.idle_percent", (decimal)Math.Round(percent, 1), Unit: "%")],
                [ActionIds.HeavyApps, ActionIds.StartupReview]
            )
        );
    }
}

/// <summary>
///     R-RUNTIMES: Visual C++ 2015-2022 is missing or older than 14.40. Programs built with
///     Visual Studio 2022 17.10 or later crash on start with an older one (msvcp140.dll).
/// </summary>
public sealed class RuntimesRule : IDiagnosticRule
{
    /// <summary>The oldest Visual C++ runtime newer programs accept.</summary>
    internal static readonly Version MinVcRuntime = new(14, 40);

    public string RuleId => RuleIds.RuntimesMissing;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.Runtimes];

    public static string DisplayName(string id) =>
        id switch
        {
            "vcredist-x64" => "Visual C++ 2015-2022 x64",
            "vcredist-x86" => "Visual C++ 2015-2022 x86",
            "netfx-4.8" => ".NET Framework 4.8",
            _ => id,
        };

    /// <summary>Installed, but a Visual C++ older than <see cref="MinVcRuntime" />.</summary>
    public static bool IsOld(RuntimeStatus runtime) =>
        runtime.Installed
        && runtime.Id.StartsWith("vcredist", StringComparison.Ordinal)
        && Version.TryParse(runtime.Version, out var v)
        && v < MinVcRuntime;

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Health.Runtimes.TryGet(out var runtimes))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Health.Runtimes.Reason,
                "runtime.state"
            );

        var evidence = new List<EvidenceFact>();
        foreach (var r in runtimes)
        {
            if (!r.Installed)
                evidence.Add(
                    new("runtime.state", Text: "not_installed", Subject: DisplayName(r.Id))
                );
            else if (IsOld(r))
                evidence.Add(
                    new(
                        "runtime.state",
                        Text: "outdated",
                        Subject: $"{DisplayName(r.Id)} {r.Version}"
                    )
                );
        }
        if (evidence.Count == 0)
            return RuleResult.Pass(RuleId);

        var vcMissing = runtimes.Any(r =>
            r.Id.StartsWith("vcredist", StringComparison.Ordinal) && !r.Installed
        );
        return RuleResult.Found(
            new Finding(
                RuleId,
                vcMissing ? Severity.Medium : Severity.Low,
                evidence,
                [ActionIds.Runtimes]
            )
        );
    }
}

/// <summary>
///     R-APP-CRASHES: programs closed by an error (Application Error 1000) at least 5 times in
///     30 days. Grouped by the faulting module: Visual C++, the video driver, .NET or Windows.
/// </summary>
public sealed partial class AppCrashesRule : IDiagnosticRule
{
    internal const int Minimum = 5;
    private const int Many = 20;
    private const int GroupMinimum = 2;

    public string RuleId => RuleIds.AppCrashes;
    public IReadOnlyList<string> PossibleActionIds { get; } =
    [ActionIds.Runtimes, ActionIds.DriverGpu];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var health = snapshot.Health;
        if (!health.AppCrashes30d.TryGet(out var total))
            return RuleResult.NotMeasurable(RuleId, health.AppCrashes30d.Reason, "crashes.total");
        if (total < Minimum)
            return RuleResult.Pass(RuleId);

        int Count(string group) =>
            health.CrashGroups.FirstOrDefault(g => g.Group == group)?.Count ?? 0;

        var evidence = new List<EvidenceFact> { new("crashes.total", total) };
        foreach (var group in new[] { "vcruntime", "gpu", "dotnet", "system" })
            if (Count(group) > 0)
                evidence.Add(new($"crashes.{group}", Count(group)));
        foreach (var app in health.CrashedApps)
            evidence.Add(new("crashes.app", app.Count, Subject: app.Name, LocalOnly: true));

        var actions = new List<string>();
        if (Count("vcruntime") >= GroupMinimum || Count("dotnet") >= GroupMinimum)
            actions.Add(ActionIds.Runtimes);
        if (Count("gpu") >= GroupMinimum)
            actions.Add(ActionIds.DriverGpu);

        return RuleResult.Found(
            new Finding(RuleId, total >= Many ? Severity.Medium : Severity.Low, evidence, actions)
        );
    }

    /// <summary>The kind of a faulting module, from its file name (msvcp140.dll -> vcruntime).</summary>
    public static string GroupOf(string? module)
    {
        var m = (module ?? string.Empty).Trim().ToLowerInvariant();
        if (m.Length == 0)
            return "other";
        if (VcRuntime().IsMatch(m))
            return "vcruntime";
        if (GpuDriver().IsMatch(m))
            return "gpu";
        if (
            m is "clr.dll" or "coreclr.dll" or "clrjit.dll"
            || m.StartsWith("system.", StringComparison.Ordinal)
        )
            return "dotnet";
        if (
            m
            is "ntdll.dll"
                or "kernelbase.dll"
                or "kernel32.dll"
                or "user32.dll"
                or "combase.dll"
                or "ucrtbase.dll"
        )
            return m == "ucrtbase.dll" ? "vcruntime" : "system";
        return "other";
    }

    [GeneratedRegex(@"^(msvcp1\d\d|vcruntime1\d\d|msvcr1\d\d|concrt1\d\d|vcomp1\d\d)(_\w+)?\.dll$")]
    private static partial Regex VcRuntime();

    // NVIDIA (nvwgf2umx, nvlddmkm, nvoglv64, nvd3dumx), AMD (atiumd*, amdxc64, amdvlk), Intel
    // (igd*, igxelpicd), and DirectX itself.
    [GeneratedRegex(
        @"^(nv\w*|ati\w*|amd\w*|igd\w*|igx\w*|igc\w*|d3d\d+\w*|dxgi|d3dcompiler_\d+)\.dll$"
    )]
    private static partial Regex GpuDriver();
}

/// <summary>R-CLOCK-OFF: the PC clock is more than 2 minutes off (HTTPS, Windows Update, games).</summary>
public sealed class ClockOffRule : IDiagnosticRule
{
    internal const double MaxSeconds = 120;
    private const double HighSeconds = 3600;

    public string RuleId => RuleIds.ClockOff;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.ClockSync];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var offset = snapshot.Health.ClockOffsetSeconds;
        if (!offset.TryGet(out var seconds))
            return RuleResult.NotMeasurable(RuleId, offset.Reason, "clock.offset_s");
        var off = Math.Abs(seconds);
        if (off <= MaxSeconds)
            return RuleResult.Pass(RuleId);
        return RuleResult.Found(
            new Finding(
                RuleId,
                off > HighSeconds ? Severity.High : Severity.Medium,
                [new("clock.offset_s", (decimal)Math.Round(seconds), Unit: "s")],
                [ActionIds.ClockSync]
            )
        );
    }
}

/// <summary>
///     R-DUMPS-BIG: driver and watchdog dumps (LiveKernelReports) take more than 1 GB. A PC
///     whose video driver keeps resetting writes one per reset; Zentra's cleanup removes them.
/// </summary>
public sealed class DumpsBigRule : IDiagnosticRule
{
    internal const long MaxBytes = 1L * 1024 * 1024 * 1024;
    private const long ManyBytes = 5L * 1024 * 1024 * 1024;

    public string RuleId => RuleIds.DumpsBig;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.DiskCleanup];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var size = snapshot.Health.LiveKernelReportsBytes;
        if (!size.TryGet(out var bytes))
            return RuleResult.NotMeasurable(RuleId, size.Reason, "dumps.live_kernel_gb");
        if (bytes <= MaxBytes)
            return RuleResult.Pass(RuleId);
        return RuleResult.Found(
            new Finding(
                RuleId,
                bytes > ManyBytes ? Severity.Medium : Severity.Low,
                [
                    new(
                        "dumps.live_kernel_gb",
                        Math.Round(bytes / (1024m * 1024 * 1024), 1),
                        Unit: "GB"
                    ),
                ],
                [ActionIds.DiskCleanup]
            )
        );
    }
}

/// <summary>
///     R-SSD-TOOL (information): the SSD maker has its own tool to update the firmware and read
///     the drive's health. Only for the makers Zentra knows; never installed by Zentra.
/// </summary>
public sealed partial class SsdToolRule : IDiagnosticRule
{
    public string RuleId => RuleIds.SsdTool;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.SsdTool];

    /// <summary>The maker's tool for an SSD model, or null when the maker is unknown.</summary>
    public static string? ToolFor(string? model)
    {
        var m = (model ?? string.Empty).Trim();
        if (m.Length == 0)
            return null;
        bool Has(string word) => m.Contains(word, StringComparison.OrdinalIgnoreCase);

        if (Has("Samsung"))
            return "Samsung Magician";
        if (Has("SanDisk"))
            return "SanDisk Dashboard";
        if (
            Has("WDC")
            || m.StartsWith("WD ", StringComparison.OrdinalIgnoreCase)
            || Has("Western Digital")
        )
            return "WD Dashboard";
        if (Has("Crucial") || Has("Micron") || CrucialModel().IsMatch(m))
            return "Crucial Storage Executive";
        if (Has("Kingston"))
            return "Kingston SSD Manager";
        if (Has("Solidigm") || m.StartsWith("INTEL SSD", StringComparison.OrdinalIgnoreCase))
            return "Solidigm Storage Tool";
        if (Has("ADATA") || Has("XPG"))
            return "ADATA SSD ToolBox";
        if (Has("SK hynix") || Has("SKHynix"))
            return "SK hynix Drive Manager";
        if (Has("Seagate"))
            return "SeaTools";
        return null;
    }

    // Crucial models are named "CT500P3SSD8", "CT1000MX500SSD1".
    [GeneratedRegex(@"^CT\d{3,4}", RegexOptions.IgnoreCase)]
    private static partial Regex CrucialModel();

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var ssds = snapshot
            .Disks.Where(d => d.Kind is StorageMediaType.Ssd or StorageMediaType.Nvme)
            .ToList();
        if (ssds.Count == 0)
            return RuleResult.NotApplicable(RuleId);

        var evidence = new List<EvidenceFact>();
        foreach (var disk in ssds)
            if (disk.Model.TryGet(out var model) && ToolFor(model) is { } tool)
                evidence.Add(new("ssd.maker_tool", Text: tool, Subject: model));
        return evidence.Count == 0
            ? RuleResult.Pass(RuleId)
            : RuleResult.Found(new Finding(RuleId, Severity.Info, evidence, [ActionIds.SsdTool]));
    }
}

/// <summary>
///     R-ANTIVIRUS: no antivirus on, one out of date, or two running at once (they slow each
///     other and the PC). Zentra never turns an antivirus off: the technician reviews it.
/// </summary>
public sealed class AntivirusRule : IDiagnosticRule
{
    public string RuleId => RuleIds.Antivirus;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.AntivirusReview];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var list = snapshot.Health.Antivirus;
        if (!list.TryGet(out var products))
            return RuleResult.NotMeasurable(RuleId, list.Reason, "av.state");

        var enabled = products.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
            return Found(Severity.High, [new("av.state", Text: "none")]);

        var outdated = enabled.Where(p => !p.UpToDate).ToList();
        if (outdated.Count > 0)
            return Found(
                Severity.Medium,
                [
                    .. outdated.Select(p => new EvidenceFact(
                        "av.state",
                        Text: "outdated",
                        Subject: p.Name
                    )),
                ]
            );

        if (enabled.Count >= 2)
            return Found(
                Severity.Medium,
                [
                    new(
                        "av.multiple",
                        enabled.Count,
                        Subject: string.Join(", ", enabled.Select(p => p.Name))
                    ),
                ]
            );
        return RuleResult.Pass(RuleId);

        RuleResult Found(Severity severity, IReadOnlyList<EvidenceFact> evidence) =>
            RuleResult.Found(new Finding(RuleId, severity, evidence, [ActionIds.AntivirusReview]));
    }

    /// <summary>
    ///     Security Center's productState: the second byte says on (0x10) or off, the third byte
    ///     up to date (0x00) or out of date (0x10). 397568 = 0x061100 is on and up to date.
    /// </summary>
    public static (bool Enabled, bool UpToDate) Decode(uint productState) =>
        ((productState & 0x1000) != 0, (productState & 0x10) == 0);
}

/// <summary>
///     R-LEAK: a program holds 100,000 handles or 8,000 GDI objects (Windows stops at 10,000):
///     it leaks, and the PC gets slower the longer it runs. Restarting or updating it helps.
/// </summary>
public sealed class LeakRule : IDiagnosticRule
{
    public const int MaxHandles = 100_000;
    public const int MaxGdiObjects = 8_000;

    public string RuleId => RuleIds.Leak;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.HeavyApps];

    public static bool IsLeaking(int handles, int gdi) =>
        handles >= MaxHandles || gdi >= MaxGdiObjects;

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var list = snapshot.Health.LeakingProcesses;
        if (!list.TryGet(out var processes))
            return RuleResult.NotMeasurable(RuleId, list.Reason, "leak.handles");
        var leaking = processes.Where(p => IsLeaking(p.Handles, p.GdiObjects)).ToList();
        if (leaking.Count == 0)
            return RuleResult.Pass(RuleId);

        var evidence = new List<EvidenceFact> { new("leak.count", leaking.Count) };
        foreach (var p in leaking.Take(3))
        {
            if (p.Handles >= MaxHandles)
                evidence.Add(new("leak.handles", p.Handles, Subject: p.Name, LocalOnly: true));
            if (p.GdiObjects >= MaxGdiObjects)
                evidence.Add(new("leak.gdi", p.GdiObjects, Subject: p.Name, LocalOnly: true));
        }
        return RuleResult.Found(
            new Finding(RuleId, Severity.Medium, evidence, [ActionIds.HeavyApps])
        );
    }
}
