using System.Text.RegularExpressions;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Diagnostics;

/// <summary>
///     Tells integrated GPUs from dedicated ones by name. When unsure it answers null (not
///     measurable) rather than a false yes or no. Name patterns must be checked on real PCs.
/// </summary>
public static partial class IntegratedGpuClassifier
{
    public static bool? Classify(
        CpuVendor cpuVendor,
        string? cpuName,
        GpuVendor gpuVendor,
        string? gpuName
    )
    {
        var gpu = gpuName?.Trim() ?? string.Empty;
        if (gpu.Length == 0)
            return null;

        switch (gpuVendor)
        {
            case GpuVendor.Nvidia:
                return false;

            case GpuVendor.Intel:
                if (ArcDiscrete().IsMatch(gpu))
                    return false; // Arc A770, B580...
                if (IntelIntegrated().IsMatch(gpu))
                    return true; // UHD, HD Graphics, Iris, "Arc Graphics" (Core Ultra)
                return null;

            case GpuVendor.Amd:
                if (AmdDiscrete().IsMatch(gpu))
                    return false;
                var cpuHasGraphics =
                    cpuVendor == CpuVendor.Amd
                    && (cpuName?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ?? false);
                if (AmdIntegrated().IsMatch(gpu))
                    return cpuVendor == CpuVendor.Amd ? true : null;
                return
                    cpuHasGraphics && gpu.EndsWith("Graphics", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : null;

            default:
                return null;
        }
    }

    // Intel UHD / HD / Iris (Xe) and Core Ultra "Arc(TM) Graphics" are integrated.
    [GeneratedRegex(
        @"\b(UHD|HD)\s+Graphics\b|\bIris\b|\bArc(\(TM\))?\s+Graphics\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex IntelIntegrated();

    // Intel Arc A-series / B-series cards ("Arc A770", "Arc B580") are dedicated.
    [GeneratedRegex(@"\bArc\b.*\b[AB]\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArcDiscrete();

    // AMD dedicated families: RX, Pro W, FirePro, R5/R7/R9 nnn, HD nnnn.
    [GeneratedRegex(
        @"\bRX\s*\d|\bPro\s+W\d|\bFirePro\b|\bR[579]\s+\d{3}|\bHD\s+\d{4}",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex AmdDiscrete();

    // AMD APU graphics: "Radeon(TM) Graphics", "Radeon Vega 8 Graphics", "Radeon 780M Graphics".
    [GeneratedRegex(
        @"Radeon(\(TM\))?\s+Graphics$|\bVega\s+\d+\s+Graphics\b|\b\d{3}M\s+Graphics\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex AmdIntegrated();
}
