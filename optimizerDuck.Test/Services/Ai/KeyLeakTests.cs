using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using optimizerDuck.Services.Ai;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Reports;
using Serilog.Extensions.Logging;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Ai;

/// <summary>
///     Plan phase 5, step 8: "the key does not appear in any generated file". Every path a key
///     could take to disk is run with a real key-shaped value: the app's own log configuration,
///     the crash file text, session.json and the PDF, through success and every failure.
/// </summary>
public sealed class KeyLeakSentinelTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("zentra-leak-");

    public void Dispose() => _root.Delete(recursive: true);

    private static Func<HttpRequestMessage, HttpResponseMessage>[] Scenarios(AiPayload payload) =>
        [
            _ => FakeHandler.Answer(TemplateReportWriter.Write(payload)),
            _ =>
                FakeHandler.Answer(
                    TemplateReportWriter.Write(payload) with
                    {
                        Summary = "Visita https://x.com",
                    }
                ),
            _ =>
                FakeHandler.Json(
                    "{\"error\":{\"type\":\"authentication_error\"}}",
                    HttpStatusCode.Unauthorized
                ),
            _ => FakeHandler.Json("{}", HttpStatusCode.InternalServerError),
            _ => FakeHandler.Json("{}", (HttpStatusCode)429),
            _ => FakeHandler.Json("not json"),
            // The worst case: an exception whose message is the request, headers included.
            request => throw new InvalidOperationException(request.ToString()),
            request => throw new HttpRequestException(request.ToString()),
        ];

    [Fact]
    public async Task TheKey_NeverReachesAnyFile()
    {
        var raw = TestKeys.New();
        var logPath = Path.Combine(_root.FullName, "zentra.log");
        var payload = AiFixtures.Payload();

        using (var serilog = App.BuildLoggerConfiguration(logPath).CreateLogger())
        using (var factory = new SerilogLoggerFactory(serilog))
        {
            // Client mode: "remember" is ignored and nothing is written.
            var keys = new ApiKeyStore(
                Path.Combine(_root.FullName, "Secrets"),
                technicianMode: false,
                factory.CreateLogger<ApiKeyStore>()
            );
            var appLogger = factory.CreateLogger("SessionViewModel");

            var i = 0;
            foreach (var scenario in Scenarios(payload))
            {
                if (!keys.HasKey)
                    Assert.Null(keys.Set(raw, remember: true));

                var service = new AiReportService(
                    keys,
                    factory.CreateLogger<AiReportService>(),
                    () => new ClaudeClient(new FakeHandler(scenario))
                );
                var record = await AiReportServiceTests.Run(service);

                var session = AiFixtures.Session() with { Report = record };
                var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, $"s{i++}"));
                File.WriteAllText(
                    Path.Combine(folder.FullName, "session.json"),
                    SessionStore.Serialize(session),
                    Encoding.UTF8
                );
                var model = SessionReportModel.Build(
                    session,
                    [],
                    _ => null,
                    Now,
                    CultureInfo.CurrentCulture
                );
                PdfReportService.Render(model, Path.Combine(folder.FullName, "informe.pdf"));
            }

            // What the page does when something throws: log it, write a crash file.
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.anthropic.com/"
            );
            ApiKeySecret.TryCreate(raw, out var key);
            key!.ApplyTo(request);
            var leaky = new InvalidOperationException(request.ToString());
            Assert.Contains(raw, leaky.Message); // the test really carries the key
            appLogger.LogError(leaky, "Report writing failed: {Detail}", request.ToString());
            File.WriteAllText(
                Path.Combine(_root.FullName, "crash.log"),
                App.CrashText("Test", leaky)
            );
        }

        var files = Directory
            .EnumerateFiles(_root.FullName, "*", SearchOption.AllDirectories)
            .ToList();
        // Daily log files since F1-11: zentra20261119.log.
        Assert.Contains(
            files,
            f =>
                Path.GetFileName(f).StartsWith("zentra", StringComparison.Ordinal)
                && f.EndsWith(".log")
        );
        Assert.Contains(files, f => f.EndsWith("informe.pdf"));
        Assert.DoesNotContain(files, f => f.Contains("dpapi"));
        Assert.All(files, f => Assert.False(Contains(f, raw), $"Key found in {f}"));
    }

    [Fact]
    public void TheScanner_FindsAKeyWhenThereIsOne()
    {
        // Positive control: the scan above would see the key in any of these encodings.
        var raw = TestKeys.New();
        var utf8 = Path.Combine(_root.FullName, "a.txt");
        var utf16 = Path.Combine(_root.FullName, "b.txt");
        var b64 = Path.Combine(_root.FullName, "c.txt");
        File.WriteAllText(utf8, $"x {raw} y", Encoding.UTF8);
        File.WriteAllText(utf16, $"x {raw} y", Encoding.Unicode);
        File.WriteAllText(b64, Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));

        Assert.True(Contains(utf8, raw));
        Assert.True(Contains(utf16, raw));
        Assert.True(Contains(b64, raw));
    }

    private static bool Contains(string path, string key)
    {
        var bytes = File.ReadAllBytes(path);
        return IndexOf(bytes, Encoding.UTF8.GetBytes(key))
            || IndexOf(bytes, Encoding.Unicode.GetBytes(key))
            || IndexOf(
                bytes,
                Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)))
            );
    }

    private static bool IndexOf(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}

public class AiPrivacyTests
{
    /// <summary>
    ///     Every key the payload may carry. A new field must be added here on purpose, after
    ///     checking that it is anonymous (plan section 8.1, "never sent").
    /// </summary>
    private static readonly HashSet<string> AllowedKeys =
    [
        "equipo",
        "windows",
        "compilacion",
        "procesador",
        "nucleos",
        "hilos",
        "ram_gb",
        "ram_tipo",
        "ram_mts",
        "graficos",
        "placa",
        "disco_sistema",
        "puntaje_antes",
        "puntaje_despues",
        "mediciones",
        "nombre",
        "antes",
        "despues",
        "diferencia",
        "mejoro",
        "hallazgos",
        "rule_id",
        "gravedad",
        "titulo",
        "descripcion",
        "evidencia",
        "acciones",
        "action_id",
        "quien",
        "no_medible",
        "aplicado",
        "accion",
        "cambios",
    ];

    [Fact]
    public void Payload_HasOnlyWhitelistedFields()
    {
        var json = JObject.Parse(AiPayloadBuilder.ToJson(AiFixtures.Payload()));

        var keys = json.DescendantsAndSelf().OfType<JProperty>().Select(p => p.Name).ToHashSet();

        Assert.Empty(keys.Except(AllowedKeys));
    }

    [Fact]
    public void OnlyTheKeyClasses_EverHoldTheKey()
    {
        // The architecture rule: ApiKeySecret appears in fields, properties or parameters of
        // these types only. A PDF, a session or a log type touching it fails here.
        Type[] allowed =
        [
            typeof(ApiKeySecret),
            typeof(ApiKeyStore),
            typeof(ClaudeClient),
            typeof(AiReportService),
        ];
        bool IsAllowed(Type? t)
        {
            for (; t is not null; t = t.DeclaringType)
                if (allowed.Contains(t))
                    return true;
            return false;
        }

        const BindingFlags all =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        var offenders = typeof(ApiKeySecret)
            .Assembly.GetTypes()
            .Where(t => !IsAllowed(t))
            .Where(t =>
                t.GetFields(all).Any(f => f.FieldType == typeof(ApiKeySecret))
                || t.GetProperties(all).Any(p => p.PropertyType == typeof(ApiKeySecret))
                || t.GetMethods(all)
                    .Cast<MethodBase>()
                    .Concat(t.GetConstructors(all))
                    .Any(m => m.GetParameters().Any(p => p.ParameterType == typeof(ApiKeySecret)))
            )
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Session_RoundTripsTheWrittenReport_WithoutAnyKey()
    {
        var payload = AiFixtures.Payload();
        var session = AiFixtures.Session() with
        {
            Report = new AiReportRecord
            {
                Source = ReportSource.Template,
                TemplateReason = TemplateReason.NoKey,
                Payload = payload,
                Report = TemplateReportWriter.Write(payload),
            },
        };

        var back = SessionStore.Deserialize(SessionStore.Serialize(session))!;

        Assert.Equal(ReportSource.Template, back.Report!.Source);
        Assert.Equal(session.Report.Report.Summary, back.Report.Report.Summary);
        Assert.Equal(payload.AllowedActionIds, back.Report.Payload.AllowedActionIds);
    }
}
