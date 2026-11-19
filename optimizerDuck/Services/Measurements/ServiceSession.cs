using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;

namespace optimizerDuck.Services.Measurements;

/// <summary>
///     The anonymous hardware lines the AI report may mention: models and sizes only, read from
///     the snapshot. No serials, names, paths or disk numbers.
/// </summary>
public sealed record HardwareSummary(
    string? Os,
    int? Build,
    string? Cpu,
    int? Cores,
    int? Threads,
    double? RamGb,
    string? RamType,
    int? RamMts,
    IReadOnlyList<string> Gpus,
    string? Board,
    string? SystemDiskKind
)
{
    public static HardwareSummary From(DiagnosticSnapshot s)
    {
        string? Text(Measured<string> m) => m.TryGet(out var v) ? v.Trim() : null;
        int? Int(Measured<int> m) => m.TryGet(out var v) ? v : null;

        var speeds = s
            .Ram.Modules.Select(m => m.ConfiguredMTps)
            .Where(m => m.HasValue)
            .Select(m => m.ValueOrDefault)
            .ToList();
        var board = string.Join(
                ' ',
                new[] { Text(s.Board.Manufacturer), Text(s.Board.Product) }.Where(t =>
                    t is not null
                )
            )
            .Trim();

        return new HardwareSummary(
            Text(s.Os.Edition),
            Int(s.Os.Build),
            Text(s.Cpu.Model),
            Int(s.Cpu.Cores),
            Int(s.Cpu.Threads),
            s.Ram.InstalledBytes.TryGet(out var bytes)
                ? Math.Round(bytes / (1024d * 1024 * 1024), 1)
                : null,
            s.Ram.Type == MemoryType.Unknown ? null : s.Ram.Type.ToString().ToUpperInvariant(),
            speeds.Count > 0 ? speeds.Min() : null,
            s.Gpus.Select(g => Text(g.Model)).OfType<string>().Distinct().ToList(),
            board.Length > 0 ? board : null,
            s.SystemVolume.Kind switch
            {
                StorageMediaType.Nvme => "NVMe",
                StorageMediaType.Ssd => "SSD",
                StorageMediaType.Hdd => "HDD",
                _ => null,
            }
        );
    }
}

/// <summary>The diagnosis part a session keeps: score and findings, not the raw snapshot.</summary>
public sealed record DiagnosisSummary(
    DateTimeOffset CapturedAtUtc,
    ScoreBreakdown Score,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<RuleResult> NotMeasurable,
    HardwareSummary? Hardware = null
)
{
    public static DiagnosisSummary From(DiagnosticReport report) =>
        new(
            report.Snapshot.CapturedAtUtc,
            report.Score,
            report.Findings.ToList(),
            report.Results.Where(r => r.Outcome == RuleOutcome.NotMeasurable).ToList(),
            HardwareSummary.From(report.Snapshot)
        );
}

/// <summary>
///     One service to one client: the "before" diagnosis and measurement, and the "after" ones.
///     The client label stays on this PC; it never goes to the AI.
/// </summary>
public sealed record ServiceSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string? ClientLabel { get; init; }
    public DiagnosisSummary? DiagnosisBefore { get; init; }
    public DiagnosisSummary? DiagnosisAfter { get; init; }
    public Measurement? Before { get; init; }
    public Measurement? After { get; init; }

    /// <summary>The written report (AI or template), with exactly what was sent to produce it.</summary>
    public Ai.AiReportRecord? Report { get; init; }
}

/// <summary>Saves sessions as JSON under %LocalAppData%\Zentra\Sessions\.</summary>
public sealed class SessionStore(ILogger<SessionStore> logger)
{
    public static string SessionsDirectory => Path.Combine(Shared.RootDirectory, "Sessions");

    public static string FolderFor(ServiceSession session) =>
        Path.Combine(
            SessionsDirectory,
            $"{session.CreatedAtUtc.ToLocalTime():yyyy-MM-dd_HHmm}_{session.Id.ToString("N")[..8]}"
        );

    public void Save(ServiceSession session)
    {
        try
        {
            var folder = FolderFor(session);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "session.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, Serialize(session), Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save session {Id}", session.Id);
        }
    }

    public static string Serialize(ServiceSession session) =>
        JsonConvert.SerializeObject(session, MeasuredJsonConverter.Settings);

    public static ServiceSession? Deserialize(string json) =>
        JsonConvert.DeserializeObject<ServiceSession>(json, MeasuredJsonConverter.Settings);
}
