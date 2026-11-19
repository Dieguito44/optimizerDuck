using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using optimizerDuck.Domain.Diagnostics;

namespace optimizerDuck.Services.Ai;

/// <summary>
///     Pulls the figures out of a text written in Spanish (or in the invariant culture). A figure
///     right after a letter is part of a name ("DDR4", "B450M", "P10.10") and is not a quantity;
///     a figure followed by a unit ("8GB", "41 s") is.
/// </summary>
public static partial class SpanishNumberExtractor
{
    public static IEnumerable<string> Tokens(string? text) =>
        string.IsNullOrEmpty(text) ? [] : NumberToken().Matches(text).Select(m => m.Value);

    /// <summary>
    ///     Every value a token can mean: "6.100" is 6100 in Spanish and 6.1 in English, "41,0" is 41.
    ///     The check accepts a figure if any reading matches, which errs on the side of the AI only
    ///     for separators, never for a different figure.
    /// </summary>
    public static IReadOnlySet<decimal> Readings(string token)
    {
        var set = new HashSet<decimal>();
        var digits = new string(token.Where(char.IsDigit).ToArray());
        if (
            decimal.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var whole)
        )
            set.Add(whole);

        var lastSep = token.LastIndexOfAny(['.', ',']);
        if (lastSep > 0)
        {
            var intPart = new string(token[..lastSep].Where(char.IsDigit).ToArray());
            var fraction = token[(lastSep + 1)..];
            if (
                decimal.TryParse(
                    $"{intPart}.{fraction}",
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var dec
                )
            )
                set.Add(dec);
        }

        return set;
    }

    // "P10.10": the lookbehind also skips a figure right after a separator, or "10" would match.
    [GeneratedRegex(@"(?<![\p{L}\d.,])\d+(?:[.,]\d+)*")]
    private static partial Regex NumberToken();
}

/// <summary>
///     Checks the AI's answer before anyone sees it (plan phase 5, step 5). Every reason is written
///     for the model: it goes back in the retry so the second answer can fix it.
/// </summary>
public static partial class AiReportValidator
{
    public const int MaxSummaryChars = 1200;
    public const int MaxExplanationChars = 600;
    public const int MaxWarnings = 6;
    public const int MaxWarningChars = 400;

    /// <summary>Small counts and fixed figures a plain explanation may use without evidence.</summary>
    private static readonly decimal[] AlwaysAllowed =
    [
        0,
        1,
        2,
        3,
        4,
        5,
        6,
        7,
        8,
        9,
        10,
        32,
        64,
        100,
    ];

    /// <summary>Words of the plan's blacklist, and tools Zentra never recommends.</summary>
    private static readonly string[] ForbiddenWords =
    [
        "activador",
        "kms",
        "crack",
        "limpiador de registro",
        "limpiadores de registro",
        "liberador de ram",
        "liberadores de ram",
        "desactivar defender",
        "desactiva defender",
        "desactivar windows defender",
        "desactivar windows update",
        "desactiva windows update",
        "desactivar el uac",
        "bcdedit",
        "archivo hosts",
        "quitar edge",
        "desinstalar edge",
        "quitar el archivo de paginación",
        "desactivar el archivo de paginación",
        "ccleaner",
        "advanced systemcare",
        "iobit",
        "driver booster",
        "razer cortex",
    ];

    public static IReadOnlyList<string> Validate(AiReport? report, AiPayload payload)
    {
        if (report is null)
            return ["La respuesta no tiene el formato pedido."];

        var problems = new List<string>();
        var allowedActions = payload.AllowedActionIds.ToHashSet(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(report.Summary))
            problems.Add("El resumen está vacío.");
        else if (report.Summary.Length > MaxSummaryChars)
            problems.Add($"El resumen pasa de {MaxSummaryChars} caracteres.");

        var priorities = report.Priorities ?? [];
        var warnings = report.Warnings ?? [];

        if (allowedActions.Count == 0 && priorities.Count > 0)
            problems.Add("No hay hallazgos: la lista de prioridades debe ir vacía.");
        if (allowedActions.Count > 0 && priorities.Count == 0)
            problems.Add("Faltan las prioridades: hay hallazgos con acciones.");

        foreach (var p in priorities)
        {
            if (!ActionCatalog.Contains(p.ActionId))
                problems.Add($"La acción {p.ActionId} no existe en el catálogo.");
            else if (!allowedActions.Contains(p.ActionId))
                problems.Add($"La acción {p.ActionId} no está en los hallazgos de este equipo.");
            if (string.IsNullOrWhiteSpace(p.Explanation))
                problems.Add($"La explicación de {p.ActionId} está vacía.");
            else if (p.Explanation.Length > MaxExplanationChars)
                problems.Add(
                    $"La explicación de {p.ActionId} pasa de {MaxExplanationChars} caracteres."
                );
        }

        if (priorities.Select(p => p.ActionId).Distinct().Count() != priorities.Count)
            problems.Add("Hay acciones repetidas en las prioridades.");
        if (
            !priorities
                .Select(p => p.Priority)
                .Order()
                .SequenceEqual(Enumerable.Range(1, priorities.Count))
        )
            problems.Add("Las prioridades deben ser 1, 2, 3... sin saltos ni repetidos.");

        if (warnings.Count > MaxWarnings)
            problems.Add($"Hay más de {MaxWarnings} advertencias.");
        foreach (var w in warnings)
            if (w.Length > MaxWarningChars)
                problems.Add($"Una advertencia pasa de {MaxWarningChars} caracteres.");

        var texts = new List<string> { report.Summary ?? string.Empty };
        texts.AddRange(priorities.Select(p => p.Explanation ?? string.Empty));
        texts.AddRange(warnings);

        problems.AddRange(CheckNumbers(texts, payload));
        problems.AddRange(CheckWords(texts));

        return problems.Distinct().ToList();
    }

    private static IEnumerable<string> CheckNumbers(IEnumerable<string> texts, AiPayload payload)
    {
        var allowed = AllowedNumbers(payload);
        var invented = texts
            .SelectMany(SpanishNumberExtractor.Tokens)
            .Where(token => !SpanishNumberExtractor.Readings(token).Overlaps(allowed))
            .Distinct()
            .ToList();
        if (invented.Count > 0)
            yield return $"Estas cifras no están en los datos: {string.Join(", ", invented)}. Usa solo las cifras recibidas, tal como vienen.";
    }

    /// <summary>
    ///     The figures the text may quote: every number in the payload (read in both cultures),
    ///     their whole and one-decimal roundings, and a short fixed list.
    /// </summary>
    public static IReadOnlySet<decimal> AllowedNumbers(AiPayload payload)
    {
        var set = new HashSet<decimal>(AlwaysAllowed)
        {
            payload.Findings.Count,
            payload.Metrics.Count,
        };
        foreach (var token in SpanishNumberExtractor.Tokens(JsonConvert.SerializeObject(payload)))
        foreach (var value in SpanishNumberExtractor.Readings(token))
        {
            set.Add(value);
            set.Add(Math.Round(value, 0, MidpointRounding.AwayFromZero));
            set.Add(Math.Round(value, 1, MidpointRounding.AwayFromZero));
        }

        return set;
    }

    private static IEnumerable<string> CheckWords(IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            var lower = text.ToLowerInvariant();
            if (UrlOrEmail().IsMatch(lower))
                yield return "No pongas enlaces, páginas web ni correos.";
            foreach (var word in ForbiddenWords)
                if (WordRegex(word).IsMatch(lower))
                    yield return $"No menciones \"{word}\": está fuera de lo que ofrece el servicio.";
        }
    }

    private static Regex WordRegex(string word) =>
        new($@"(?<!\p{{L}}){Regex.Escape(word)}(?!\p{{L}})", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"https?://|www\.|\b[\w.-]+@[\w-]+\.\w+|\b[\w-]+\.(com|net|org|co|io|es)\b")]
    private static partial Regex UrlOrEmail();
}
