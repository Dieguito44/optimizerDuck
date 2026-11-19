using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Services.System;

namespace optimizerDuck.Domain.Diagnostics.Rules;

/// <summary>Shared figures and helpers for the rules.</summary>
internal static class RuleMath
{
    public const long GiB = 1024L * 1024 * 1024;

    public static decimal Gb(long bytes) => Math.Round(bytes / (decimal)GiB, 1);

    public static string ModuleSubject(RamModuleSnapshot module) => module.Slot;

    public static string DiskSubject(DiskSnapshot disk) =>
        disk.Model.TryGet(out var model) ? model : $"Disk {disk.Number}";
}

/// <summary>
///     R-RAM-XMP: the RAM runs below the kit's rated speed (XMP/EXPO off). The rated speed comes
///     from the kit table by part number, or from SMBIOS when it reports more than the configured
///     speed. Without either, the rule is not measurable: speed alone never proves XMP is off.
/// </summary>
public sealed class RamXmpRule : IDiagnosticRule
{
    /// <summary>Tolerance for firmware rounding (3597 vs 3600).</summary>
    private const decimal Tolerance = 0.95m;

    public string RuleId => RuleIds.RamXmp;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BiosXmp];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        var modules = snapshot.Ram.Modules;
        if (modules.Count == 0)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, "ram.modules");

        var evidence = new List<EvidenceFact>();
        var unmeasured = new List<string>();
        var slow = false;

        foreach (var module in modules)
        {
            var subject = RuleMath.ModuleSubject(module);
            if (!module.ConfiguredMTps.TryGet(out var configured))
            {
                unmeasured.Add($"ram.configured_mts:{subject}");
                continue;
            }

            int? rated = null;
            string? source = null;
            if (refs.LookupRamKit(module.PartNumber.ValueOrDefault) is { } kit)
            {
                rated = kit.RatedMTps;
                source = "table";
            }
            else if (module.ReportedMTps.TryGet(out var reported) && reported > configured)
            {
                rated = reported;
                source = "smbios";
            }

            if (rated is null)
            {
                unmeasured.Add($"ram.rated_mts:{subject}");
                continue;
            }

            evidence.Add(new("ram.configured_mts", configured, Unit: "MT/s", Subject: subject));
            evidence.Add(new("ram.rated_mts", rated, source, "MT/s", subject));
            if (configured < rated.Value * Tolerance)
                slow = true;
        }

        if (evidence.Count == 0)
            return RuleResult.NotMeasurable(
                RuleId,
                NotMeasuredReason.NotInReferenceTable,
                [.. unmeasured]
            );

        return slow
            ? RuleResult.Found(
                new Finding(RuleId, Severity.High, evidence, [ActionIds.BiosXmp]),
                [.. unmeasured]
            )
            : RuleResult.Pass(RuleId, [.. unmeasured]);
    }
}

/// <summary>R-RAM-SINGLE: one RAM module (single channel). Critical on integrated graphics.</summary>
public sealed class RamSingleRule : IDiagnosticRule
{
    public string RuleId => RuleIds.RamSingle;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.HardwareRamDual];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (snapshot.Ram.Type == MemoryType.Lpddr)
            return RuleResult.NotApplicable(RuleId); // soldered RAM: nothing to add

        var count = snapshot.Ram.Modules.Count;
        if (count == 0)
            return RuleResult.NotMeasurable(RuleId, NotMeasuredReason.NotReported, "ram.modules");
        if (count > 1)
            return RuleResult.Pass(RuleId);

        var igpuOnly = snapshot.IntegratedGraphicsOnly is { HasValue: true, ValueOrDefault: true };
        return RuleResult.Found(
            new Finding(
                RuleId,
                igpuOnly ? Severity.Critical : Severity.High,
                [
                    new("ram.module_count", count),
                    new("gpu.integrated_only", Text: igpuOnly ? "yes" : "no"),
                ],
                [ActionIds.HardwareRamDual]
            )
        );
    }
}

/// <summary>R-RAM-LOW: less than 8 GB installed (installed, not what Windows sees).</summary>
public sealed class RamLowRule : IDiagnosticRule
{
    private const long Minimum = 8 * RuleMath.GiB;

    public string RuleId => RuleIds.RamLow;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.HardwareRamDual];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.Ram.InstalledBytes.TryGet(out var installed))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Ram.InstalledBytes.Reason,
                "ram.installed_bytes"
            );

        return installed < Minimum
            ? RuleResult.Found(
                new Finding(
                    RuleId,
                    Severity.High,
                    [new("ram.installed_gb", RuleMath.Gb(installed), Unit: "GB")],
                    [ActionIds.HardwareRamDual]
                )
            )
            : RuleResult.Pass(RuleId);
    }
}

/// <summary>
///     R-APU-UMA: an AMD APU with 512 MB or less reserved for graphics on 16 GB or more of RAM.
///     Uses the installed RAM, because the UMA reservation hides part of it from Windows.
/// </summary>
public sealed class ApuUmaRule : IDiagnosticRule
{
    private const int MaxUmaMb = 512;
    private const long MinInstalled = 16 * RuleMath.GiB;

    public string RuleId => RuleIds.ApuUma;
    public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.BiosUma2G];

    public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs)
    {
        if (!snapshot.IntegratedGraphicsOnly.TryGet(out var igpuOnly))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.IntegratedGraphicsOnly.Reason,
                "gpu.integrated_only"
            );
        if (!igpuOnly || snapshot.Cpu.Vendor != CpuVendor.Amd)
            return RuleResult.NotApplicable(RuleId);

        var apu = snapshot.Gpus.FirstOrDefault(g =>
            g.Vendor == GpuVendor.Amd && g.IsIntegrated is { HasValue: true, ValueOrDefault: true }
        );
        if (apu is null)
            return RuleResult.NotApplicable(RuleId);

        if (!apu.DedicatedMB.TryGet(out var uma))
            return RuleResult.NotMeasurable(RuleId, apu.DedicatedMB.Reason, "gpu.dedicated_mb");
        if (!snapshot.Ram.InstalledBytes.TryGet(out var installed))
            return RuleResult.NotMeasurable(
                RuleId,
                snapshot.Ram.InstalledBytes.Reason,
                "ram.installed_bytes"
            );

        if (uma > MaxUmaMb || installed < MinInstalled)
            return RuleResult.Pass(RuleId);

        return RuleResult.Found(
            new Finding(
                RuleId,
                Severity.High,
                [
                    new("gpu.dedicated_mb", uma, Unit: "MB"),
                    new("ram.installed_gb", RuleMath.Gb(installed), Unit: "GB"),
                ],
                [ActionIds.BiosUma2G]
            )
        );
    }
}
