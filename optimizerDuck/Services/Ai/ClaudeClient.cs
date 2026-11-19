using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace optimizerDuck.Services.Ai;

/// <summary>Why a call to the API did not give a usable answer. Never carries the API's text.</summary>
public enum ClaudeFailure
{
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
}

public sealed record ClaudeMessage(string Role, string Text);

public sealed record ClaudeResult(
    AiReport? Report,
    ClaudeFailure? Failure,
    string? Model,
    int? InputTokens,
    int? OutputTokens,
    string? RawJson
);

/// <summary>
///     The one class that talks to the Claude API, over plain HttpClient (plan phase 5, step 3).
///     The answer's shape is forced with structured outputs (a JSON schema), which works on
///     Sonnet 5.5; a forced tool_choice does not. Rules this class keeps:
///     the key goes on each request, never on DefaultRequestHeaders; requests and responses are
///     never logged or put in an exception (HttpRequestMessage.ToString prints the headers);
///     no EnsureSuccessStatusCode (its message carries the response).
/// </summary>
public sealed class ClaudeClient : IDisposable
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string ApiVersion = "2026-10-21";
    public const int MaxTokens = 8000;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);
    private static readonly Uri Endpoint = new("https://api.anthropic.com/v1/messages");

    private readonly HttpClient _http;

    public ClaudeClient()
        : this(
            new SocketsHttpHandler
            {
                // A proxy set on a client's PC could read the request: go direct.
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
            }
        ) { }

    internal ClaudeClient(HttpMessageHandler handler, string model = DefaultModel)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Model = model;
    }

    public string Model { get; }

    public void Dispose() => _http.Dispose();

    public async Task<ClaudeResult> WriteReportAsync(
        ApiKeySecret key,
        string system,
        IReadOnlyList<ClaudeMessage> messages,
        IReadOnlyList<string> allowedActionIds,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                BuildBody(Model, system, messages, allowedActionIds).ToString(Formatting.None),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        key.ApplyTo(request);

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Fail(FailureFor(response.StatusCode));
            var body = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            return Parse(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(ClaudeFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return Fail(ClaudeFailure.Network);
        }
        finally
        {
            response?.Dispose();
        }
    }

    internal static ClaudeFailure FailureFor(HttpStatusCode status) =>
        (int)status switch
        {
            401 or 403 => ClaudeFailure.InvalidKey,
            429 => ClaudeFailure.RateLimited,
            529 => ClaudeFailure.Overloaded,
            >= 500 => ClaudeFailure.ServerError,
            _ => ClaudeFailure.BadRequest,
        };

    internal static JObject BuildBody(
        string model,
        string system,
        IReadOnlyList<ClaudeMessage> messages,
        IReadOnlyList<string> allowedActionIds
    ) =>
        new()
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["messages"] = new JArray(
                messages.Select(m => new JObject { ["role"] = m.Role, ["content"] = m.Text })
            ),
            ["output_config"] = new JObject
            {
                ["effort"] = "medium",
                ["format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = ReportSchema(allowedActionIds),
                },
            },
        };

    /// <summary>
    ///     The schema of plan section 8.2. When there are allowed actions, action_id is an enum of
    ///     exactly those, so the API itself cannot answer with another one.
    /// </summary>
    internal static JObject ReportSchema(IReadOnlyList<string> allowedActionIds)
    {
        var actionId = new JObject { ["type"] = "string" };
        if (allowedActionIds.Count > 0)
            actionId["enum"] = new JArray(allowedActionIds);

        return new JObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JArray("resumen", "prioridades", "advertencias"),
            ["properties"] = new JObject
            {
                ["resumen"] = new JObject { ["type"] = "string" },
                ["prioridades"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JArray("action_id", "prioridad", "explicacion"),
                        ["properties"] = new JObject
                        {
                            ["action_id"] = actionId,
                            ["prioridad"] = new JObject { ["type"] = "integer" },
                            ["explicacion"] = new JObject { ["type"] = "string" },
                        },
                    },
                },
                ["advertencias"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                },
            },
        };
    }

    internal static ClaudeResult Parse(string body)
    {
        JObject root;
        try
        {
            root = JObject.Parse(body);
        }
        catch (JsonException)
        {
            return Fail(ClaudeFailure.MalformedResponse);
        }

        var model = root.Value<string>("model");
        var usage = root["usage"] as JObject;
        var input = usage?.Value<int?>("input_tokens");
        var output = usage?.Value<int?>("output_tokens");

        ClaudeResult Failed(ClaudeFailure f) => new(null, f, model, input, output, null);

        switch (root.Value<string>("stop_reason"))
        {
            case "refusal":
                return Failed(ClaudeFailure.Refused);
            case "max_tokens":
                return Failed(ClaudeFailure.Truncated);
        }

        var text = (root["content"] as JArray)
            ?.OfType<JObject>()
            .Where(b => b.Value<string>("type") == "text")
            .Select(b => b.Value<string>("text"))
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (text is null)
            return Failed(ClaudeFailure.MalformedResponse);

        try
        {
            var report = JsonConvert.DeserializeObject<AiReport>(text);
            return report is null
                ? Failed(ClaudeFailure.MalformedResponse)
                : new ClaudeResult(report, null, model, input, output, text);
        }
        catch (JsonException)
        {
            return Failed(ClaudeFailure.MalformedResponse);
        }
    }

    private static ClaudeResult Fail(ClaudeFailure failure) =>
        new(null, failure, null, null, null, null);
}
