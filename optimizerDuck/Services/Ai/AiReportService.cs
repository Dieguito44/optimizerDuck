using Microsoft.Extensions.Logging;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Reports;

namespace optimizerDuck.Services.Ai;

/// <summary>
///     Writes the session report: with the AI when there is a key and its answer passes the checks,
///     with the template otherwise. At most two calls per report: the first, and one retry that
///     tells the model what was wrong. Logs only outcomes and counts, never texts or the key.
/// </summary>
public sealed class AiReportService(
    ApiKeyStore keys,
    ILogger<AiReportService> logger,
    Func<ClaudeClient>? clientFactory = null
)
{
    public const int MaxCalls = 2;

    public const string SystemPrompt = """
        Eres un técnico de computadores que le explica a un cliente colombiano, sin conocimientos técnicos, qué se encontró en su PC y qué se hizo. Recibes los datos de la revisión en JSON: el equipo, el puntaje de 0 a 100 antes y después, las mediciones, los hallazgos con sus acciones (action_id) y lo que se aplicó.

        Reglas:
        - Usa solo los action_id que aparecen en los hallazgos. No agregues acciones, productos, programas ni páginas web.
        - Toda cifra que escribas debe estar en los datos, tal como viene (por ejemplo "41,0 s" o "6.100 MB"). No calcules porcentajes ni cifras nuevas: un programa revisa cada número y rechaza el informe si alguno no está en los datos.
        - Si un dato no se pudo medir, dilo así; no lo adivines.
        - Frases cortas, tuteo y español de Colombia. Evita los tecnicismos; si usas uno (BIOS, SSD, RAM), explícalo en pocas palabras.
        - resumen: de 3 a 5 frases sobre cómo estaba el equipo y cómo quedó.
        - prioridades: una por cada acción que valga la pena, de la más importante (1) a la menos importante, sin repetir. La explicación dice qué es, por qué le importa al cliente y quién la hace (campo "quien").
        - advertencias: lo que el cliente debe saber (riesgos, datos que no se pudieron medir, cosas que dependen de él). Puede ir vacía.
        """;

    private readonly Func<ClaudeClient> _clientFactory =
        clientFactory ?? (() => new ClaudeClient());

    public async Task<AiReportRecord> WriteAsync(
        ServiceSession session,
        SessionReportModel model,
        CancellationToken cancellationToken
    )
    {
        var payload = AiPayloadBuilder.Build(session, model);
        var key = keys.Current;
        if (key is null)
            return Template(payload, TemplateReason.NoKey, 0, null, null, null);

        var messages = new List<ClaudeMessage>
        {
            new(
                "user",
                $"Datos de la revisión:\n{AiPayloadBuilder.ToJson(payload)}\n\nEscribe el informe para el cliente."
            ),
        };

        using var client = _clientFactory();
        int input = 0,
            output = 0;
        string? servedModel = null;

        for (var attempt = 1; attempt <= MaxCalls; attempt++)
        {
            var result = await client
                .WriteReportAsync(
                    key,
                    SystemPrompt,
                    messages,
                    payload.AllowedActionIds,
                    cancellationToken
                )
                .ConfigureAwait(false);
            input += result.InputTokens ?? 0;
            output += result.OutputTokens ?? 0;
            servedModel = result.Model ?? servedModel;

            if (result.Failure is { } failure)
            {
                logger.LogWarning("AI report call {Attempt} failed: {Failure}", attempt, failure);
                if (failure == ClaudeFailure.InvalidKey)
                    keys.Clear();
                return Template(payload, ReasonFor(failure), attempt, servedModel, input, output);
            }

            var problems = AiReportValidator.Validate(result.Report, payload);
            if (problems.Count == 0)
            {
                logger.LogInformation(
                    "AI report accepted on call {Attempt} ({Input} in, {Output} out tokens)",
                    attempt,
                    input,
                    output
                );
                return new AiReportRecord
                {
                    Source = ReportSource.Ai,
                    Model = servedModel,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Attempts = attempt,
                    InputTokens = input,
                    OutputTokens = output,
                    Payload = payload,
                    Report = result.Report!,
                };
            }

            logger.LogWarning(
                "AI report call {Attempt} rejected by the checks ({Count} problems)",
                attempt,
                problems.Count
            );
            messages.Add(new ClaudeMessage("assistant", result.RawJson ?? "{}"));
            messages.Add(
                new ClaudeMessage(
                    "user",
                    "Tu informe no pasó la revisión automática por esto:\n- "
                        + string.Join("\n- ", problems)
                        + "\nEscríbelo de nuevo corrigiendo esos puntos."
                )
            );
        }

        return Template(payload, TemplateReason.Rejected, MaxCalls, servedModel, input, output);
    }

    private static TemplateReason ReasonFor(ClaudeFailure failure) =>
        failure switch
        {
            ClaudeFailure.InvalidKey => TemplateReason.InvalidKey,
            ClaudeFailure.RateLimited => TemplateReason.RateLimited,
            ClaudeFailure.Overloaded => TemplateReason.Overloaded,
            ClaudeFailure.ServerError => TemplateReason.ServerError,
            ClaudeFailure.BadRequest => TemplateReason.BadRequest,
            ClaudeFailure.Timeout => TemplateReason.Timeout,
            ClaudeFailure.Network => TemplateReason.Network,
            ClaudeFailure.Truncated => TemplateReason.Truncated,
            ClaudeFailure.Refused => TemplateReason.Refused,
            _ => TemplateReason.MalformedResponse,
        };

    private static AiReportRecord Template(
        AiPayload payload,
        TemplateReason reason,
        int attempts,
        string? model,
        int? input,
        int? output
    ) =>
        new()
        {
            Source = ReportSource.Template,
            TemplateReason = reason,
            Model = model,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Attempts = attempts,
            InputTokens = attempts > 0 ? input : null,
            OutputTokens = attempts > 0 ? output : null,
            Payload = payload,
            Report = TemplateReportWriter.Write(payload),
        };
}
