using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Ai;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Reports;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Ai;

/// <summary>Plan of improvements F1-07: the client's consent and the minimized payload.</summary>
public class AiConsentTests
{
    private static SessionReportModel Model(ServiceSession session) =>
        SessionReportModel.Build(session, [], _ => null, Now, CultureInfo.CurrentCulture);

    [Fact]
    public async Task WithoutConsent_NothingIsSent_AndTheTemplateSaysWhy()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "zentra-consent-" + Guid.NewGuid().ToString("N")
        );
        var keys = new ApiKeyStore(dir, technicianMode: true, NullLogger<ApiKeyStore>.Instance);
        keys.Set(TestKeys.New(), remember: false);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no call"));
        var service = new AiReportService(
            keys,
            NullLogger<AiReportService>.Instance,
            () => new ClaudeClient(handler)
        );
        var session = AiFixtures.Session() with { AiConsent = null };

        var record = await service.WriteAsync(session, Model(session), CancellationToken.None);

        Assert.Equal(ReportSource.Template, record.Source);
        Assert.Equal(TemplateReason.NoConsent, record.TemplateReason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AConsentToAnOlderText_DoesNotCount()
    {
        var session = AiFixtures.Session() with
        {
            AiConsent = new AiConsentRecord { TextVersion = "old", AcceptedAtUtc = Now },
        };
        Assert.False(AiConsent.IsGiven(session));
        Assert.True(AiConsent.IsGiven(AiFixtures.Session()));
    }

    [Fact]
    public void TheConsent_IsKeptInSessionJson_WithItsTextVersionAndTime()
    {
        var session = AiFixtures.Session();
        var back = SessionStore.Deserialize(SessionStore.Serialize(session))!;
        Assert.Equal(AiConsent.TextVersion, back.AiConsent!.TextVersion);
        Assert.Equal(Now, back.AiConsent.AcceptedAtUtc);
    }

    [Fact]
    public void TheConsentText_NamesWhoGetsTheData_WhereAndForHowLong()
    {
        var text = optimizerDuck.Services.Configuration.Loc.Instance[AiConsent.TextKey];
        Assert.Contains("Anthropic", text);
        Assert.Contains("30", text);
        Assert.NotEqual(AiConsent.TextKey, text);
    }

    [Fact]
    public void Redact_TakesOutNamesSerialsPathsAndAddresses()
    {
        var text = PersonalData.Redact(
            @"DESKTOP-ANA de Ana López, serial PF3XK29Q, en C:\Users\ana\Documents, ana@correo.co, 192.168.1.20, Windows 10.0.26200.1",
            ["DESKTOP-ANA", "Ana López"],
            ["PF3XK29Q"]
        );
        Assert.DoesNotContain("DESKTOP-ANA", text);
        Assert.DoesNotContain("Ana López", text);
        Assert.DoesNotContain("PF3XK29Q", text);
        Assert.DoesNotContain(@"\ana\", text);
        Assert.DoesNotContain("ana@correo.co", text);
        Assert.DoesNotContain("192.168.1.20", text);
        Assert.Contains("10.0.26200.1", text); // Windows' version stays
        Assert.Contains(PersonalData.SerialPlaceholder, text);
    }

    [Fact]
    public void Redact_KeepsTermsTooShortToBeSafe()
    {
        // A two-letter name would eat "SSD", "RAM"... in every text.
        Assert.Equal("RAM DDR4", PersonalData.Redact("RAM DDR4", ["RA"], []));
    }

    [Fact]
    public void ThePayload_GoesThroughTheRedactor_InEveryText()
    {
        var payload = AiFixtures.Payload();
        var name = payload
            .Findings[0]
            .Title.Split(' ')
            .First(w => w.Length >= 4 && w.All(char.IsLetter));

        var scrubbed = AiPayloadBuilder.Scrub(payload, new PersonalTerms([name], []));

        Assert.DoesNotContain(name, scrubbed.Findings[0].Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PersonalData.NamePlaceholder, scrubbed.Findings[0].Title);
        // Numbers and ids stay: the AI's checks need them.
        Assert.Equal(payload.ScoreBefore, scrubbed.ScoreBefore);
        Assert.Equal(payload.AllowedActionIds, scrubbed.AllowedActionIds);
    }

    [Fact]
    public void ThePayload_NeverCarriesThisPcsNames()
    {
        var json = AiPayloadBuilder.ToJson(AiFixtures.Payload());
        foreach (var name in PersonalData.LocalNames().Where(n => n.Length >= 3))
            Assert.DoesNotContain(name, json, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Plan of improvements F1-08: the report through files, the key off the client's PC.</summary>
public class AiExchangeTests : IDisposable
{
    private readonly string _dir = Directory
        .CreateDirectory(
            Path.Combine(Path.GetTempPath(), "zentra-exchange-" + Guid.NewGuid().ToString("N"))
        )
        .FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AiReportRecord AiRecord(AiPayload payload, AiReport? report = null) =>
        new()
        {
            Source = ReportSource.Ai,
            Model = "claude-test",
            CreatedAtUtc = Now,
            Attempts = 1,
            InputTokens = 100,
            OutputTokens = 50,
            Payload = payload,
            Report =
                report
                ?? new AiReport(
                    "El equipo quedó mejor.",
                    [new AiPriority(payload.AllowedActionIds[0], 1, "Es lo primero.")],
                    []
                ),
        };

    /// <summary>Client → file → technician → file → client, as the two PCs do it.</summary>
    private (ServiceSession Session, AiPayload Payload, string Json) RoundTrip(
        AiReport? report = null
    )
    {
        var session = AiFixtures.Session();
        var payload = AiFixtures.Payload(session);

        var requestPath = Path.Combine(_dir, AiRequestFile.SuggestedName);
        AiExchange.Write(requestPath, AiExchange.Request(session, payload));
        var request = AiExchange.ReadRequest(requestPath);
        Assert.NotNull(request);

        var result = AiExchange.Result(request, AiRecord(request.Payload, report));
        return (session, payload, AiExchange.Serialize(result));
    }

    [Fact]
    public void TheReportComesBack_ForTheSameSessionAndPayload()
    {
        var (session, payload, json) = RoundTrip();

        var (record, error) = AiExchange.Import(json, session, payload);

        Assert.Null(error);
        Assert.Equal(ReportSource.Ai, record!.Source);
        Assert.Equal("El equipo quedó mejor.", record.Report.Summary);
    }

    [Fact]
    public void TheRequestFile_HoldsNoClientLabel_NoCulprits_AndNoKey()
    {
        var session = AiFixtures.Session();
        var json = AiExchange.Serialize(AiExchange.Request(session, AiFixtures.Payload(session)));
        Assert.DoesNotContain(AiFixtures.ClientCanary, json);
        Assert.DoesNotContain(AiFixtures.CulpritCanary, json);
        Assert.DoesNotContain("sk-ant", json);
    }

    [Fact]
    public void NoRequest_WithoutConsent()
    {
        var session = AiFixtures.Session() with { AiConsent = null };
        Assert.Throws<InvalidOperationException>(() =>
            AiExchange.Request(session, AiFixtures.Payload(session))
        );
    }

    [Fact]
    public void AnEditedRequest_IsRefusedOnTheTechniciansPc()
    {
        var session = AiFixtures.Session();
        var path = Path.Combine(_dir, AiRequestFile.SuggestedName);
        AiExchange.Write(path, AiExchange.Request(session, AiFixtures.Payload(session)));
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("\"puntaje_antes\": ", "\"puntaje_antes\": 1")
        );

        Assert.Null(AiExchange.ReadRequest(path));
    }

    [Fact]
    public void AReportForAnotherSession_IsRefused()
    {
        var (session, payload, json) = RoundTrip();
        var other = session with { Id = Guid.NewGuid() };

        Assert.Equal(AiImportError.OtherSession, AiExchange.Import(json, other, payload).Error);
    }

    [Fact]
    public void AReportFromOtherData_IsRefused()
    {
        var (session, _, json) = RoundTrip();
        // Measured again after the export: the payload is not the one the report was written from.
        var newer = session with
        {
            After = session.After! with { BootMedianMs = Measured<int>.Of(5000) },
        };

        Assert.Equal(
            AiImportError.OtherPayload,
            AiExchange.Import(json, newer, AiFixtures.Payload(newer)).Error
        );
    }

    [Fact]
    public void AReportThatFailsZentrasChecks_IsRefused()
    {
        var (session, payload, json) = RoundTrip(
            new AiReport(
                "Hay que desactivar Defender para que vaya rápido.",
                [new AiPriority("ACT-INVENTED", 1, "Una acción inventada.")],
                []
            )
        );

        Assert.Equal(AiImportError.Rejected, AiExchange.Import(json, session, payload).Error);
    }

    [Fact]
    public void WithoutConsentOnTheClientsPc_TheReportIsRefused()
    {
        var (session, payload, json) = RoundTrip();
        Assert.Equal(
            AiImportError.NoConsent,
            AiExchange.Import(json, session with { AiConsent = null }, payload).Error
        );
    }

    [Theory]
    [InlineData("not json at all {")]
    [InlineData("{\"Format\": \"something-else\"}")]
    [InlineData("null")]
    public void AnythingElse_IsRefused(string json)
    {
        var session = AiFixtures.Session();
        var error = AiExchange.Import(json, session, AiFixtures.Payload(session)).Error;
        Assert.True(error is AiImportError.Unreadable or AiImportError.WrongFile);
    }

    [Fact]
    public void AHugeFile_IsNotRead()
    {
        var path = Path.Combine(_dir, "big.json");
        File.WriteAllText(path, new string(' ', (int)AiExchange.MaxBytes + 1));
        Assert.Null(AiExchange.ReadRequest(path));
        var session = AiFixtures.Session();
        Assert.Equal(
            AiImportError.Unreadable,
            AiExchange.ImportFile(path, session, AiFixtures.Payload(session)).Error
        );
    }
}
