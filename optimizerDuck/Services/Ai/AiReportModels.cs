using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Reports;

namespace optimizerDuck.Services.Ai;

// ---------------------------------------------------------------------------------------------
// What goes to the API (plan section 8.1). Built from a whitelist: every field below is named on
// purpose, so a new field in the session never reaches the AI by accident. Texts are the same
// Spanish strings the PDF shows, so the AI quotes the figures exactly as the client reads them.
// Never sent: the client label, dates, the session id, boot culprits (app names), disk numbers.
// ---------------------------------------------------------------------------------------------

public sealed record AiPayload
{
    [JsonProperty("equipo")]
    public AiHardware? Hardware { get; init; }

    [JsonProperty("puntaje_antes")]
    public int? ScoreBefore { get; init; }

    [JsonProperty("puntaje_despues")]
    public int? ScoreAfter { get; init; }

    [JsonProperty("mediciones")]
    public IReadOnlyList<AiMetric> Metrics { get; init; } = [];

    [JsonProperty("hallazgos")]
    public IReadOnlyList<AiFinding> Findings { get; init; } = [];

    [JsonProperty("no_medible")]
    public IReadOnlyList<string> NotMeasured { get; init; } = [];

    [JsonProperty("aplicado")]
    public IReadOnlyList<AiApplied> Applied { get; init; } = [];

    /// <summary>The only action ids the report may name: those of the findings, in the catalog.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllowedActionIds =>
        Findings.SelectMany(f => f.Actions.Select(a => a.ActionId)).Distinct().ToList();
}

public sealed record AiHardware
{
    [JsonProperty("windows")]
    public string? Os { get; init; }

    [JsonProperty("compilacion")]
    public int? Build { get; init; }

    [JsonProperty("procesador")]
    public string? Cpu { get; init; }

    [JsonProperty("nucleos")]
    public int? Cores { get; init; }

    [JsonProperty("hilos")]
    public int? Threads { get; init; }

    [JsonProperty("ram_gb")]
    public double? RamGb { get; init; }

    [JsonProperty("ram_tipo")]
    public string? RamType { get; init; }

    [JsonProperty("ram_mts")]
    public int? RamMts { get; init; }

    [JsonProperty("graficos")]
    public IReadOnlyList<string> Gpus { get; init; } = [];

    [JsonProperty("placa")]
    public string? Board { get; init; }

    [JsonProperty("disco_sistema")]
    public string? SystemDiskKind { get; init; }
}

public sealed record AiMetric(
    [property: JsonProperty("nombre")] string Name,
    [property: JsonProperty("antes")] string Before,
    [property: JsonProperty("despues")] string After,
    [property: JsonProperty("diferencia")] string Difference,
    [property: JsonProperty("mejoro")] bool? Improved
);

public sealed record AiFinding(
    [property: JsonProperty("rule_id")] string RuleId,
    [property: JsonProperty("gravedad")] string Severity,
    [property: JsonProperty("titulo")] string Title,
    [property: JsonProperty("descripcion")] string Description,
    [property: JsonProperty("evidencia")] IReadOnlyList<string> Evidence,
    [property: JsonProperty("acciones")] IReadOnlyList<AiAction> Actions
);

public sealed record AiAction(
    [property: JsonProperty("action_id")] string ActionId,
    [property: JsonProperty("titulo")] string Title,
    [property: JsonProperty("quien")] string Who
);

public sealed record AiApplied(
    [property: JsonProperty("accion")] string Name,
    [property: JsonProperty("cambios")] int Changes
);

// ---------------------------------------------------------------------------------------------
// What comes back (plan section 8.2), forced by a JSON schema.
// ---------------------------------------------------------------------------------------------

public sealed record AiReport(
    [property: JsonProperty("resumen")] string Summary,
    [property: JsonProperty("prioridades")] IReadOnlyList<AiPriority> Priorities,
    [property: JsonProperty("advertencias")] IReadOnlyList<string> Warnings
);

public sealed record AiPriority(
    [property: JsonProperty("action_id")] string ActionId,
    [property: JsonProperty("prioridad")] int Priority,
    [property: JsonProperty("explicacion")] string Explanation
);

public enum ReportSource
{
    Ai,
    Template,
}

/// <summary>Why the template wrote the report instead of the AI. Stored, never the API's own text.</summary>
public enum TemplateReason
{
    NoKey,
    InvalidKey,
    RateLimited,
    Overloaded,
    ServerError,
    BadRequest,
    Timeout,
    Network,
    MalformedResponse,
    Truncated,
    Refused,
    Rejected,

    /// <summary>Plan F1-07: the client did not accept sending the data (Ley 1581).</summary>
    NoConsent,
}

/// <summary>The report kept in session.json: the text, who wrote it, and what was sent.</summary>
public sealed record AiReportRecord
{
    public ReportSource Source { get; init; }
    public TemplateReason? TemplateReason { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public int Attempts { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public required AiPayload Payload { get; init; }
    public required AiReport Report { get; init; }
}

/// <summary>Builds the payload from a session and the report model the PDF uses.</summary>
public static class AiPayloadBuilder
{
    /// <summary>
    ///     The payload with this PC's names and serial taken out (plan F1-07). The whitelist
    ///     already leaves them out; this is the second net, for a name that slips into a text
    ///     (a finding's evidence, a device name).
    /// </summary>
    public static AiPayload Build(ServiceSession session, SessionReportModel model) =>
        Build(session, model, PersonalTerms.For(session));

    public static AiPayload Build(
        ServiceSession session,
        SessionReportModel model,
        PersonalTerms terms
    ) => Scrub(Raw(session, model), terms);

    /// <summary>Every text of the payload through <see cref="PersonalData.Redact(string, IEnumerable{string?}, IEnumerable{string?})" />.</summary>
    public static AiPayload Scrub(AiPayload payload, PersonalTerms terms)
    {
        string S(string text) => PersonalData.Redact(text, terms.Names, terms.Serials);
        string? N(string? text) => text is null ? null : S(text);
        return payload with
        {
            Hardware = payload.Hardware is not { } hw
                ? null
                : hw with
                {
                    Os = N(hw.Os),
                    Cpu = N(hw.Cpu),
                    RamType = N(hw.RamType),
                    Gpus = hw.Gpus.Select(S).ToList(),
                    Board = N(hw.Board),
                    SystemDiskKind = N(hw.SystemDiskKind),
                },
            Metrics = payload
                .Metrics.Select(m =>
                    m with
                    {
                        Name = S(m.Name),
                        Before = S(m.Before),
                        After = S(m.After),
                        Difference = S(m.Difference),
                    }
                )
                .ToList(),
            Findings = payload
                .Findings.Select(f =>
                    f with
                    {
                        Title = S(f.Title),
                        Description = S(f.Description),
                        Evidence = f.Evidence.Select(S).ToList(),
                        Actions = f
                            .Actions.Select(a => a with { Title = S(a.Title), Who = S(a.Who) })
                            .ToList(),
                    }
                )
                .ToList(),
            NotMeasured = payload.NotMeasured.Select(S).ToList(),
            Applied = payload.Applied.Select(a => a with { Name = S(a.Name) }).ToList(),
        };
    }

    private static AiPayload Raw(ServiceSession session, SessionReportModel model)
    {
        var latest = session.DiagnosisAfter ?? session.DiagnosisBefore;
        var hw = latest?.Hardware;

        return new AiPayload
        {
            Hardware = hw is null
                ? null
                : new AiHardware
                {
                    Os = hw.Os,
                    Build = hw.Build,
                    Cpu = hw.Cpu,
                    Cores = hw.Cores,
                    Threads = hw.Threads,
                    RamGb = hw.RamGb,
                    RamType = hw.RamType,
                    RamMts = hw.RamMts,
                    Gpus = hw.Gpus,
                    Board = hw.Board,
                    SystemDiskKind = hw.SystemDiskKind,
                },
            ScoreBefore = model.ScoreBefore,
            ScoreAfter = model.ScoreAfter,
            Metrics = model
                .Metrics.Select(m => new AiMetric(
                    m.Name,
                    m.Before,
                    m.After,
                    m.Difference,
                    m.Improved
                ))
                .ToList(),
            Findings =
                latest
                    ?.Findings.OrderByDescending(f => f.Severity)
                    .Select(f =>
                    {
                        var view = FindingTextRenderer.Render(f, forAi: true);
                        return new AiFinding(
                            f.RuleId,
                            view.SeverityText,
                            view.Title,
                            view.Description,
                            view.Evidence,
                            f.ActionIds.Where(ActionCatalog.Contains)
                                .Distinct()
                                .Select(id => new AiAction(
                                    id,
                                    FindingTextRenderer.ActionTitle(f.RuleId, id),
                                    Loc.Instance[$"Ai.Who.{ActionCatalog.All[id].Kind}"]
                                ))
                                .ToList()
                        );
                    })
                    .ToList()
                ?? [],
            NotMeasured = model.NotMeasured,
            Applied = model.Applied.Select(a => new AiApplied(a.Name, a.Changes)).ToList(),
        };
    }

    /// <summary>SHA-256 of the payload's JSON: ties a report written elsewhere to this payload.</summary>
    public static string Sha256(AiPayload payload) =>
        Convert
            .ToHexString(
                global::System.Security.Cryptography.SHA256.HashData(
                    global::System.Text.Encoding.UTF8.GetBytes(ToJson(payload))
                )
            )
            .ToLowerInvariant();

    public static string ToJson(AiPayload payload) =>
        JsonConvert.SerializeObject(
            payload,
            new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
            }
        );
}

/// <summary>
///     The fallback writer: no internet, no key, an API error or two rejected answers. It uses only
///     the payload, so it can never say anything the checks would reject.
/// </summary>
public static class TemplateReportWriter
{
    public const int MaxPriorities = 8;

    public static AiReport Write(AiPayload payload)
    {
        var summary = new List<string>();
        if (payload is { ScoreBefore: { } before, ScoreAfter: { } after })
            summary.Add(Loc.Instance["Ai.Template.ScoreBoth", before, after]);
        else if ((payload.ScoreAfter ?? payload.ScoreBefore) is { } score)
            summary.Add(Loc.Instance["Ai.Template.ScoreOne", score]);

        var improved = payload.Metrics.Where(m => m.Improved == true).ToList();
        if (improved.Count > 0)
            summary.Add(
                Loc.Instance[
                    "Ai.Template.Improved",
                    string.Join(
                        "; ",
                        improved.Select(m =>
                            Loc.Instance["Ai.Template.Change", m.Name, m.Before, m.After]
                        )
                    )
                ]
            );

        summary.Add(
            payload.Findings.Count == 0
                ? Loc.Instance["Ai.Template.NoFindings"]
                : Loc.Instance["Ai.Template.Findings", payload.Findings.Count]
        );

        var priorities = new List<AiPriority>();
        foreach (var finding in payload.Findings)
        foreach (var action in finding.Actions)
        {
            if (priorities.Count >= MaxPriorities)
                break;
            if (priorities.Any(p => p.ActionId == action.ActionId))
                continue;
            priorities.Add(
                new AiPriority(
                    action.ActionId,
                    priorities.Count + 1,
                    Loc.Instance["Ai.Template.Priority", action.Title, finding.Title]
                )
            );
        }

        // What could not be measured is a limit of Zentra, not a problem of the client's PC: it
        // goes to the technical annex at the end of the PDF, never among the warnings in orange
        // (version 1 test, 20/11/2026).
        return new AiReport(string.Join(" ", summary), priorities, []);
    }
}
