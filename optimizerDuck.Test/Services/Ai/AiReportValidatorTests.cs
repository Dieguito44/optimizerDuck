using optimizerDuck.Services.Ai;

namespace optimizerDuck.Test.Services.Ai;

public class SpanishNumberExtractorTests
{
    [Theory]
    [InlineData("Arranca en 41,0 s", new[] { "41,0" })]
    [InlineData("Usa 6.100 MB en reposo", new[] { "6.100" })]
    [InlineData("Memoria DDR4 en una placa B450M con BIOS P10.10", new string[0])]
    [InlineData("Tiene 8GB y un Ryzen 5 5600GT", new[] { "8", "5", "5600" })]
    [InlineData("Bajó -2,0 s", new[] { "2,0" })]
    public void Tokens_SkipNamesButKeepQuantities(string text, string[] expected)
    {
        Assert.Equal(expected, SpanishNumberExtractor.Tokens(text));
    }

    [Theory]
    [InlineData("6.100", 6100)]
    [InlineData("6.100", 6.1)]
    [InlineData("41,0", 41)]
    [InlineData("3.478,9", 3478.9)]
    [InlineData("3,478.9", 3478.9)]
    public void Readings_CoverBothCultures(string token, double expected)
    {
        Assert.Contains((decimal)expected, SpanishNumberExtractor.Readings(token));
    }
}

public class AiReportValidatorTests
{
    private static AiReport Good(AiPayload payload) =>
        new(
            "El equipo quedó listo. Arrancaba en 9,0 s y ahora en 7 s.",
            payload
                .AllowedActionIds.Take(2)
                .Select((id, i) => new AiPriority(id, i + 1, "Hazlo primero porque ayuda."))
                .ToList(),
            ["Hay datos que no se pudieron medir."]
        );

    [Fact]
    public void AGoodAnswer_Passes()
    {
        var payload = AiFixtures.Payload();
        Assert.Empty(AiReportValidator.Validate(Good(payload), payload));
    }

    [Fact]
    public void TheTemplate_AlwaysPassesTheChecks()
    {
        var payload = AiFixtures.Payload();
        Assert.Empty(AiReportValidator.Validate(TemplateReportWriter.Write(payload), payload));
    }

    [Fact]
    public void AnInventedAction_IsRejected()
    {
        var payload = AiFixtures.Payload();
        var report = Good(payload) with
        {
            Priorities = [new AiPriority("ACT-INVENTADA", 1, "Algo.")],
        };

        Assert.Contains(
            AiReportValidator.Validate(report, payload),
            p => p.Contains("ACT-INVENTADA")
        );
    }

    [Fact]
    public void ACatalogActionThatIsNotInTheFindings_IsRejected()
    {
        var payload = AiFixtures.Payload();
        var outside = optimizerDuck.Domain.Diagnostics.ActionCatalog.All.Keys.First(id =>
            !payload.AllowedActionIds.Contains(id)
        );
        var report = Good(payload) with { Priorities = [new AiPriority(outside, 1, "Algo.")] };

        Assert.Contains(
            AiReportValidator.Validate(report, payload),
            p => p.Contains("no está en los hallazgos")
        );
    }

    [Theory]
    [InlineData("Ahora arranca un 73,9 % más rápido.")]
    [InlineData("Ganó 4.321 MB de memoria libre.")]
    [InlineData("Tiene 987,6 GB libres.")]
    public void AnInventedFigure_IsRejected(string summary)
    {
        var payload = AiFixtures.Payload();
        var report = Good(payload) with { Summary = summary };

        Assert.Contains(
            AiReportValidator.Validate(report, payload),
            p => p.StartsWith("Estas cifras no están en los datos")
        );
    }

    [Theory]
    [InlineData("Descarga la guía en https://ejemplo.com")]
    [InlineData("Escríbenos a soporte@ejemplo.co")]
    [InlineData("Instala CCleaner para limpiar.")]
    [InlineData("Conviene desactivar Windows Update.")]
    [InlineData("Usa un limpiador de registro.")]
    public void LinksAndBlacklistedAdvice_AreRejected(string warning)
    {
        var payload = AiFixtures.Payload();
        var report = Good(payload) with { Warnings = [warning] };

        Assert.NotEmpty(AiReportValidator.Validate(report, payload));
    }

    [Fact]
    public void BrokenPriorities_AreRejected()
    {
        var payload = AiFixtures.Payload();
        var id = payload.AllowedActionIds[0];
        var report = Good(payload) with
        {
            Priorities = [new AiPriority(id, 1, "Uno."), new AiPriority(id, 3, "Otra vez.")],
        };

        var problems = AiReportValidator.Validate(report, payload);

        Assert.Contains(problems, p => p.Contains("repetidas"));
        Assert.Contains(problems, p => p.Contains("sin saltos"));
    }

    [Fact]
    public void NoFindings_RequiresAnEmptyPriorityList()
    {
        var payload = AiFixtures.Payload() with { Findings = [] };
        var report = new AiReport(
            "Todo bien.",
            [new AiPriority(optimizerDuck.Domain.Diagnostics.ActionIds.Trim, 1, "Algo.")],
            []
        );

        Assert.NotEmpty(AiReportValidator.Validate(report, payload));
    }

    [Fact]
    public void ANullAnswer_IsRejected()
    {
        Assert.NotEmpty(AiReportValidator.Validate(null, AiFixtures.Payload()));
    }
}

public class AiReportValidatorCharacterTests
{
    [Theory]
    [InlineData("Tu PC quedó lista 🚀")]
    [InlineData("Arranque: antes → después")]
    public void SymbolsTheReportFontLacks_AreRejected(string summary)
    {
        var payload = AiFixtures.Payload();
        var report = new AiReport(summary, [], []);

        Assert.Contains(
            AiReportValidator.Validate(report, payload with { Findings = [] }),
            p => p.Contains("sin emojis")
        );
    }

    [Fact]
    public void SpanishLettersAndQuotes_ArePrintable()
    {
        Assert.All("áéíóúñÁÉÍÓÚÑ¿¡ü“”—…", c => Assert.True(AiReportValidator.IsPrintable(c)));
    }
}
