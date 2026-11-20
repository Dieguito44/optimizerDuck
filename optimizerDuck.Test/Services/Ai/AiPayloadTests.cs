using System.Globalization;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Services.Ai;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Reports;
using optimizerDuck.Test.Services.Measurements;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Ai;

/// <summary>A session with canaries: values that must never reach the AI.</summary>
internal static class AiFixtures
{
    public const string ClientCanary = "ClienteCanario Pérez";
    public const string CulpritCanary = "CanaryPersonalApp.exe";

    public static ServiceSession Session()
    {
        var report = DiagnosticReport.Create(DiegoPc(), Refs, DiagnosticRules.All);
        var before = SessionPersistenceTests.Before() with
        {
            BootCulprits = [new BootCulprit(CulpritCanary, 101, 4200)],
        };
        return new ServiceSession
        {
            CreatedAtUtc = Now,
            ClientLabel = ClientCanary,
            AiConsent = AiConsent.Accept(Now),
            Before = before,
            After = before with
            {
                Phase = MeasurementPhase.After,
                BootMedianMs = Measured<int>.Of(7000),
            },
            DiagnosisBefore = DiagnosisSummary.From(report),
        };
    }

    public static AiPayload Payload(ServiceSession? session = null)
    {
        session ??= Session();
        var model = SessionReportModel.Build(
            session,
            [],
            _ => null,
            Now,
            CultureInfo.CurrentCulture
        );
        return AiPayloadBuilder.Build(session, model);
    }
}

public class AiPayloadTests
{
    [Fact]
    public void Payload_NeverCarriesTheClientLabel_BootCulprits_OrTheSessionId()
    {
        var session = AiFixtures.Session();

        var json = AiPayloadBuilder.ToJson(AiFixtures.Payload(session));

        Assert.DoesNotContain(AiFixtures.ClientCanary, json);
        Assert.DoesNotContain(AiFixtures.CulpritCanary, json);
        Assert.DoesNotContain(session.Id.ToString(), json);
        Assert.DoesNotContain(session.Id.ToString("N"), json);
        Assert.DoesNotContain("2026-11-19", json); // no dates of the session
    }

    [Fact]
    public void Payload_HasTheFindings_TheMetrics_AndTheHardware()
    {
        var payload = AiFixtures.Payload();

        Assert.NotEmpty(payload.Findings);
        Assert.Equal(8, payload.Metrics.Count);
        Assert.NotNull(payload.Hardware);
        Assert.False(string.IsNullOrEmpty(payload.Hardware!.Cpu));
        Assert.Contains(payload.Metrics, m => m.Improved == true);
    }

    [Fact]
    public void AllowedActions_AreTheFindingsActions_AndAllInTheCatalog()
    {
        var payload = AiFixtures.Payload();

        Assert.NotEmpty(payload.AllowedActionIds);
        Assert.All(payload.AllowedActionIds, id => Assert.True(ActionCatalog.Contains(id)));
        Assert.Equal(payload.AllowedActionIds.Count, payload.AllowedActionIds.Distinct().Count());
    }

    [Fact]
    public void OldSessionsWithoutHardware_StillBuildAPayload()
    {
        var session = AiFixtures.Session();
        session = session with
        {
            DiagnosisBefore = session.DiagnosisBefore! with { Hardware = null },
        };

        var payload = AiFixtures.Payload(session);

        Assert.Null(payload.Hardware);
        Assert.NotEmpty(payload.Findings);
    }
}

public class TemplateReportWriterTests
{
    [Fact]
    public void Template_UsesOnlyAllowedActions_InOrder_WithoutRepeats()
    {
        var payload = AiFixtures.Payload();

        var report = TemplateReportWriter.Write(payload);

        Assert.NotEmpty(report.Priorities);
        Assert.All(report.Priorities, p => Assert.Contains(p.ActionId, payload.AllowedActionIds));
        Assert.Equal(
            Enumerable.Range(1, report.Priorities.Count),
            report.Priorities.Select(p => p.Priority)
        );
        Assert.Equal(
            report.Priorities.Count,
            report.Priorities.Select(p => p.ActionId).Distinct().Count()
        );
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
    }

    [Fact]
    public void Template_WithoutFindings_HasNoPriorities()
    {
        var payload = AiFixtures.Payload() with { Findings = [] };

        var report = TemplateReportWriter.Write(payload);

        Assert.Empty(report.Priorities);
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
    }
}
