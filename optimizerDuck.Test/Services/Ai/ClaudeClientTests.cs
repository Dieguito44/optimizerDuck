using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using optimizerDuck.Services.Ai;

namespace optimizerDuck.Test.Services.Ai;

/// <summary>Answers each request with the next canned response, and keeps what it was sent.</summary>
internal sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] answers)
    : HttpMessageHandler
{
    private int _next;
    public List<(string? Key, string? Version, JObject Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Requests.Add(
            (
                request.Headers.TryGetValues("x-api-key", out var k) ? k.Single() : null,
                request.Headers.TryGetValues("anthropic-version", out var v) ? v.Single() : null,
                body
            )
        );
        var answer = answers[Math.Min(_next++, answers.Length - 1)];
        return answer(request);
    }

    public static HttpResponseMessage Json(
        string json,
        HttpStatusCode status = HttpStatusCode.OK
    ) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A Messages API answer whose text block is the report.</summary>
    public static HttpResponseMessage Answer(AiReport report, string stopReason = "end_turn") =>
        Json(
            new JObject
            {
                ["model"] = "claude-sonnet-5-5",
                ["stop_reason"] = stopReason,
                ["usage"] = new JObject { ["input_tokens"] = 1500, ["output_tokens"] = 400 },
                ["content"] = new JArray(
                    new JObject { ["type"] = "thinking", ["thinking"] = "" },
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = JsonConvert.SerializeObject(report),
                    }
                ),
            }.ToString()
        );
}

public class ClaudeClientTests
{
    private static ApiKeySecret Key(out string raw)
    {
        raw = TestKeys.New();
        ApiKeySecret.TryCreate(raw, out var key);
        return key!;
    }

    private static Task<ClaudeResult> Call(FakeHandler handler, IReadOnlyList<string>? ids = null)
    {
        var client = new ClaudeClient(handler);
        return client.WriteReportAsync(
            Key(out _),
            "s",
            [new ClaudeMessage("user", "x")],
            ids ?? [],
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Request_HasKeyVersionSchemaAndNoTemperature()
    {
        var payload = AiFixtures.Payload();
        var handler = new FakeHandler(_ => FakeHandler.Answer(TemplateReportWriter.Write(payload)));
        using var client = new ClaudeClient(handler);

        var result = await client.WriteReportAsync(
            Key(out var raw),
            "sistema",
            [new ClaudeMessage("user", "hola")],
            payload.AllowedActionIds,
            CancellationToken.None
        );

        Assert.Null(result.Failure);
        Assert.NotNull(result.Report);
        Assert.Equal(1500, result.InputTokens);
        var (key, version, body) = Assert.Single(handler.Requests);
        Assert.Equal(raw, key);
        Assert.Equal(ClaudeClient.ApiVersion, version);
        Assert.Equal("claude-sonnet-5-5", body.Value<string>("model"));
        Assert.Null(body["temperature"]);
        Assert.Null(body["tool_choice"]);
        Assert.Equal("json_schema", body["output_config"]!["format"]!.Value<string>("type"));
        var enumIds = body.SelectToken(
                    "output_config.format.schema.properties.prioridades.items.properties.action_id.enum"
                )!
            .Values<string>();
        Assert.Equal(payload.AllowedActionIds, enumIds);
        Assert.DoesNotContain(raw, body.ToString()); // the key is a header, never in the body
    }

    [Theory]
    [InlineData(401, ClaudeFailure.InvalidKey)]
    [InlineData(403, ClaudeFailure.InvalidKey)]
    [InlineData(429, ClaudeFailure.RateLimited)]
    [InlineData(529, ClaudeFailure.Overloaded)]
    [InlineData(500, ClaudeFailure.ServerError)]
    [InlineData(400, ClaudeFailure.BadRequest)]
    public async Task HttpErrors_MapToAFailure(int status, ClaudeFailure expected)
    {
        var result = await Call(
            new FakeHandler(_ => FakeHandler.Json("{\"type\":\"error\"}", (HttpStatusCode)status))
        );

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Report);
    }

    [Theory]
    [InlineData("refusal", ClaudeFailure.Refused)]
    [InlineData("max_tokens", ClaudeFailure.Truncated)]
    public async Task StopReasons_AreFailures(string stopReason, ClaudeFailure expected)
    {
        var payload = AiFixtures.Payload();

        var result = await Call(
            new FakeHandler(_ =>
                FakeHandler.Answer(TemplateReportWriter.Write(payload), stopReason)
            )
        );

        Assert.Equal(expected, result.Failure);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"content\":[]}")]
    [InlineData("{\"content\":[{\"type\":\"text\",\"text\":\"esto no es JSON\"}]}")]
    public async Task GarbageAnswers_AreMalformed(string body)
    {
        var result = await Call(new FakeHandler(_ => FakeHandler.Json(body)));

        Assert.Equal(ClaudeFailure.MalformedResponse, result.Failure);
    }

    [Theory]
    [InlineData("{\"resumen\":\"ok\",\"prioridades\":[],\"advertencias\":[null]}")]
    [InlineData("{\"resumen\":\"ok\",\"prioridades\":[null],\"advertencias\":[]}")]
    [InlineData(
        "{\"resumen\":\"ok\",\"prioridades\":[{\"action_id\":null,\"prioridad\":1,\"explicacion\":\"x\"}],\"advertencias\":[]}"
    )]
    [InlineData("{\"resumen\":null,\"prioridades\":[],\"advertencias\":[]}")]
    [InlineData("{\"resumen\":\"ok\"}")]
    public async Task AnswersWithNulls_AreMalformed_NotACrash(string reportJson)
    {
        var body = new JObject
        {
            ["stop_reason"] = "end_turn",
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = reportJson }),
        }.ToString();

        var result = await Call(new FakeHandler(_ => FakeHandler.Json(body)));

        Assert.Equal(ClaudeFailure.MalformedResponse, result.Failure);
    }

    [Fact]
    public async Task NetworkErrors_AreCaught()
    {
        var result = await Call(new FakeHandler(_ => throw new HttpRequestException("sin red")));

        Assert.Equal(ClaudeFailure.Network, result.Failure);
    }
}

public class AiReportServiceTests
{
    private static (AiReportService Service, FakeHandler Handler, ApiKeyStore Keys) Make(
        params Func<HttpRequestMessage, HttpResponseMessage>[] answers
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), "zentra-svc-" + Guid.NewGuid().ToString("N"));
        var keys = new ApiKeyStore(dir, false, NullLogger<ApiKeyStore>.Instance);
        var handler = new FakeHandler(answers);
        var service = new AiReportService(
            keys,
            NullLogger<AiReportService>.Instance,
            () => new ClaudeClient(handler)
        );
        return (service, handler, keys);
    }

    internal static Task<AiReportRecord> Run(AiReportService service)
    {
        var session = AiFixtures.Session();
        var model = optimizerDuck.Services.Reports.SessionReportModel.Build(
            session,
            [],
            _ => null,
            optimizerDuck.Test.TestDoubles.SnapshotFactory.Now,
            System.Globalization.CultureInfo.CurrentCulture
        );
        return service.WriteAsync(session, model, CancellationToken.None);
    }

    private static AiReport Good(AiPayload payload) =>
        new(
            "El equipo quedó mejor.",
            [new AiPriority(payload.AllowedActionIds[0], 1, "Es lo primero.")],
            []
        );

    [Fact]
    public async Task WithoutAKey_UsesTheTemplate_AndCallsNothing()
    {
        var (service, handler, _) = Make(_ => throw new InvalidOperationException());

        var record = await Run(service);

        Assert.Equal(ReportSource.Template, record.Source);
        Assert.Equal(TemplateReason.NoKey, record.TemplateReason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AGoodAnswer_IsUsed_AfterOneCall()
    {
        var payload = AiFixtures.Payload();
        var (service, handler, keys) = Make(_ => FakeHandler.Answer(Good(payload)));
        keys.Set(TestKeys.New(), false);

        var record = await Run(service);

        Assert.Equal(ReportSource.Ai, record.Source);
        Assert.Equal(1, record.Attempts);
        Assert.Single(handler.Requests);
        Assert.Equal("El equipo quedó mejor.", record.Report.Summary);
    }

    [Fact]
    public async Task ARejectedAnswer_IsRetriedOnce_WithTheReasons()
    {
        var payload = AiFixtures.Payload();
        var bad = Good(payload) with { Summary = "Ahora es un 73,9 % más rápido." };
        var (service, handler, keys) = Make(
            _ => FakeHandler.Answer(bad),
            _ => FakeHandler.Answer(Good(payload))
        );
        keys.Set(TestKeys.New(), false);

        var record = await Run(service);

        Assert.Equal(ReportSource.Ai, record.Source);
        Assert.Equal(2, record.Attempts);
        var retry = handler.Requests[1].Body["messages"]!.Last!.Value<string>("content")!;
        Assert.Contains("73,9", retry);
        Assert.Equal(3000, record.InputTokens);
    }

    [Fact]
    public async Task TwoRejectedAnswers_FallBackToTheTemplate_AfterExactlyTwoCalls()
    {
        var payload = AiFixtures.Payload();
        var bad = Good(payload) with { Summary = "Visita https://ejemplo.com" };
        var (service, handler, keys) = Make(_ => FakeHandler.Answer(bad));
        keys.Set(TestKeys.New(), false);

        var record = await Run(service);

        Assert.Equal(ReportSource.Template, record.Source);
        Assert.Equal(TemplateReason.Rejected, record.TemplateReason);
        Assert.Equal(AiReportService.MaxCalls, handler.Requests.Count);
    }

    [Fact]
    public async Task AnInvalidKey_IsDroppedFromMemory()
    {
        var (service, _, keys) = Make(_ => FakeHandler.Json("{}", HttpStatusCode.Unauthorized));
        keys.Set(TestKeys.New(), false);

        var record = await Run(service);

        Assert.Equal(TemplateReason.InvalidKey, record.TemplateReason);
        Assert.False(keys.HasKey);
    }
}
