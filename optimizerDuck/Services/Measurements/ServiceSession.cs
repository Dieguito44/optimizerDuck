using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Diagnostics;

namespace optimizerDuck.Services.Measurements;

/// <summary>The diagnosis part a session keeps: score and findings, not the raw snapshot.</summary>
public sealed record DiagnosisSummary(
    DateTimeOffset CapturedAtUtc,
    ScoreBreakdown Score,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<RuleResult> NotMeasurable
)
{
    public static DiagnosisSummary From(DiagnosticReport report) =>
        new(
            report.Snapshot.CapturedAtUtc,
            report.Score,
            report.Findings.ToList(),
            report.Results.Where(r => r.Outcome == RuleOutcome.NotMeasurable).ToList()
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
