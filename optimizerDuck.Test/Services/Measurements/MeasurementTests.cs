using System.Collections;
using System.Globalization;
using System.IO;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Reports;
using optimizerDuck.Test.TestDoubles;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Measurements;

public class BootTimeProbeTests
{
    [Theory]
    [InlineData(new[] { 6000 }, 6000)]
    [InlineData(new[] { 9000, 6000, 7000 }, 7000)]
    [InlineData(new[] { 10000, 20000 }, 15000)]
    public void Median_IsTheMiddleValue(int[] values, int expected)
    {
        Assert.Equal(expected, BootTimeProbe.Median(values));
    }

    [Fact]
    public void PairBoots_MatchesEachStartWithTheFirstLogonBeforeTheNextStart()
    {
        var day = new DateTime(2026, 11, 19, 7, 0, 0, DateTimeKind.Utc);
        DateTime[] starts = [day, day.AddHours(5), day.AddDays(1)];
        DateTime[] logons =
        [
            day.AddSeconds(6),
            day.AddSeconds(40), // second logon of the same boot: ignored
            day.AddHours(5).AddSeconds(12),
            // no logon after the last start (the PC was turned off at the login screen)
        ];

        var boots = BootTimeProbe.PairBoots(starts, logons);

        Assert.Equal([12000, 6000], boots);
    }

    [Fact]
    public void PairBoots_IgnoresALogonHoursLater()
    {
        var start = new DateTime(2026, 11, 19, 7, 0, 0, DateTimeKind.Utc);
        Assert.Empty(BootTimeProbe.PairBoots([start], [start.AddHours(2)]));
    }

    [Fact]
    public void ParseData_ReadsEventDataByName()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <EventData><Data Name="BootTime">41000</Data><Data Name="Name">x.exe</Data></EventData>
            </Event>
            """;

        var data = BootTimeProbe.ParseData(xml);

        Assert.Equal("41000", data["BootTime"]);
        Assert.Equal("x.exe", data["Name"]);
        Assert.Empty(BootTimeProbe.ParseData("not xml"));
    }

    [Fact]
    public void BootSlow_OverOneMinute_IsMedium()
    {
        var slow = DiegoPc() with { BootMedianMs = Measured<int>.Of(75_000) };

        var result = new BootSlowRule().Evaluate(slow, Refs);

        Assert.Equal(Severity.Medium, result.Finding!.Severity);
        Assert.Contains(result.Finding.Evidence, e => e.Key == "boot.median_s" && e.Number == 75);
        Assert.Equal(RuleOutcome.Pass, new BootSlowRule().Evaluate(DiegoPc(), Refs).Outcome);
    }
}

public class DiskBenchmarkTests
{
    private const string Sample = """
        Total IO
        thread |       bytes     |     I/Os     |    MiB/s   |  I/O per s |  file
        ------------------------------------------------------------------------------
             0 |     10976493568 |        10468 |    3478.91 |    3478.91 | probe.dat (64MiB)
        ------------------------------------------------------------------------------
        total:       10976493568 |        10468 |    3478.91 |    3478.91

        Read IO
        total:       10976493568 |        10468 |    3478.91 |    3478.91
        """;

    [Fact]
    public void ParseTotals_ReadsMiBpsAndIops()
    {
        var totals = DiskBenchmark.ParseTotals(Sample);

        Assert.NotNull(totals);
        Assert.Equal(3478.91, totals!.MiBps, 2);
        Assert.Equal(3478.91, totals.Iops, 2);
    }

    [Fact]
    public void ParseTotals_WithoutATotalRow_IsNull()
    {
        Assert.Null(DiskBenchmark.ParseTotals("Error opening file"));
    }

    [Fact]
    public void DiskSpd_ShipsWithTheAppAndItsLicense()
    {
        // The build copies the MIT-built tool next to the app (Tools\diskspd.exe).
        var root = AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent)
        {
            var tool = Path.Combine(dir.FullName, "optimizerDuck", "Resources", "Tools", "DiskSpd");
            if (Directory.Exists(tool))
            {
                Assert.True(File.Exists(Path.Combine(tool, "diskspd.exe")));
                Assert.Contains("MIT", File.ReadAllText(Path.Combine(tool, "LICENSE.txt")));
                return;
            }
        }
        Assert.Fail("Resources/Tools/DiskSpd not found");
    }
}

public class SessionPersistenceTests
{
    private static Measurement Before() =>
        new()
        {
            Phase = MeasurementPhase.Before,
            CapturedAtUtc = Now,
            FastStartupEnabled = Measured<bool>.Of(true),
            BootMedianMs = Measured<int>.Of(9000),
            BootSource = Measured<BootTimeSource>.Of(BootTimeSource.KernelToLogon),
            BootSamples = 3,
            IdleRamUsedBytes = Measured<long>.Of(6_100L * 1024 * 1024),
            IdleRamPercent = Measured<double>.Of(39.6),
            SystemFreeBytes = Measured<long>.Of(173 * GiB),
            StartupAppsEnabled = Measured<int>.Of(8),
            ProcessCount = Measured<int>.Of(210),
            RunningServices = Measured<int>.Of(120),
            SequentialReadMiBps = Measured<double>.Of(3478.9),
            RandomReadIops = Measured<double>.Missing(NotMeasuredReason.RequiresExternalTool),
        };

    [Fact]
    public void Session_RoundTripsThroughJson_KeepingNotMeasuredReasons()
    {
        var report = DiagnosticReport.Create(DiegoPc(), Refs, DiagnosticRules.All);
        var session = new ServiceSession
        {
            CreatedAtUtc = Now,
            ClientLabel = "Cliente de prueba",
            Before = Before(),
            DiagnosisBefore = DiagnosisSummary.From(report),
        };

        var back = SessionStore.Deserialize(SessionStore.Serialize(session))!;

        Assert.Equal(9000, back.Before!.BootMedianMs.ValueOrDefault);
        Assert.False(back.Before.RandomReadIops.HasValue);
        Assert.Equal(NotMeasuredReason.RequiresExternalTool, back.Before.RandomReadIops.Reason);
        Assert.Equal(BootTimeSource.KernelToLogon, back.Before.BootSource.ValueOrDefault);
        Assert.Equal(report.Score.Score, back.DiagnosisBefore!.Score.Score);
        Assert.Equal(
            report.Findings.Select(f => f.RuleId),
            back.DiagnosisBefore.Findings.Select(f => f.RuleId)
        );
    }

    [Fact]
    public void Metrics_ShowChangeAndWhetherItImproved()
    {
        var after = Before() with
        {
            Phase = MeasurementPhase.After,
            BootMedianMs = Measured<int>.Of(7000),
            StartupAppsEnabled = Measured<int>.Of(4),
            SequentialReadMiBps = Measured<double>.Of(3478.9),
        };

        var rows = SessionReportModel.BuildMetrics(Before(), after, CultureInfo.InvariantCulture);

        var boot = rows[0];
        Assert.Equal("9.0 s", boot.Before);
        Assert.Equal("7.0 s", boot.After);
        Assert.Equal("-2.0 s", boot.Difference);
        Assert.True(boot.Improved);

        var startup = rows[3];
        Assert.Equal("-4", startup.Difference);
        Assert.True(startup.Improved);

        var sequential = rows[6];
        Assert.Null(sequential.Improved); // no change
        var random = rows[7];
        Assert.Equal("—", random.Difference);
        Assert.NotEqual("—", random.Before); // "not measurable", not a dash
    }

    [Fact]
    public void Report_ListsOnlyActionsAppliedDuringTheSession()
    {
        var session = new ServiceSession { CreatedAtUtc = Now, Before = Before() };
        var during = new ChangeRecord
        {
            Id = Guid.NewGuid(),
            LogName = "Disable Telemetry",
            AppliedAt = Now.UtcDateTime.AddMinutes(10),
            Steps = [new ChangeRecordStep { Kind = ChangeKind.Change, Ok = true }],
        };
        var earlier = during with { Id = Guid.NewGuid(), AppliedAt = Now.UtcDateTime.AddDays(-3) };

        var model = SessionReportModel.Build(
            session,
            [during, earlier],
            id => id == during.Id ? "Desactivar la telemetría" : null,
            Now.AddHours(1),
            CultureInfo.InvariantCulture
        );

        var applied = Assert.Single(model.Applied);
        Assert.Equal("Desactivar la telemetría", applied.Name);
    }

    [Fact]
    public void Pdf_IsWrittenFromAModel()
    {
        var report = DiagnosticReport.Create(DiegoPc(), Refs, DiagnosticRules.All);
        var session = new ServiceSession
        {
            CreatedAtUtc = Now,
            ClientLabel = "Prueba Ñandú",
            Before = Before(),
            DiagnosisBefore = DiagnosisSummary.From(report),
        };
        var path = Path.Combine(Path.GetTempPath(), $"zentra-test-{Guid.NewGuid():N}.pdf");
        var previous = optimizerDuck.Services.Configuration.Loc.CurrentCulture;
        var checkGlyphs = QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable;

        try
        {
            // Spanish text, dashes, bullets and accents must all exist in the bundled font.
            optimizerDuck.Services.Configuration.Loc.Instance.ChangeCulture(
                new CultureInfo("es-ES")
            );
            QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = true;
            var model = SessionReportModel.Build(
                session,
                [],
                _ => null,
                Now,
                new CultureInfo("es-CO")
            );

            PdfReportService.Render(model, path);

            var head = new byte[4];
            using (var stream = File.OpenRead(path))
                stream.ReadExactly(head);
            Assert.Equal("%PDF"u8.ToArray(), head);
        }
        finally
        {
            QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = checkGlyphs;
            optimizerDuck.Services.Configuration.Loc.Instance.ChangeCulture(previous);
            File.Delete(path);
        }
    }
}

/// <summary>Guards against UTF-8 text saved as Windows-1252 ("DiagnÃ³stico").</summary>
public class SpanishTextEncodingTests
{
    [Fact]
    public void SpanishTranslations_HaveNoBrokenAccents()
    {
        var set = Translations.ResourceManager.GetResourceSet(
            new CultureInfo("es-ES"),
            true,
            false
        )!;
        var broken = set.Cast<DictionaryEntry>()
            .Where(e =>
                e.Value is string s && (s.Contains('Ã') || s.Contains("â€") || s.Contains("Â¿"))
            )
            .Select(e => (string)e.Key)
            .ToList();

        Assert.True(broken.Count == 0, "Broken accents: " + string.Join(", ", broken));
    }
}
