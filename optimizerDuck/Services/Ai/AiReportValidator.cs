using System.Globalization;
using System.Text;
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
        if (texts.Any(t => t.Any(c => !IsPrintable(c))))
            problems.Add(
                "Usa solo letras, números y signos comunes: sin emojis, flechas ni símbolos."
            );

        return problems.Distinct().ToList();
    }

    /// <summary>
    ///     The PDF font (Lato) covers Latin text and common punctuation; anything else would stop the
    ///     PDF from rendering, so the answer may not contain it.
    /// </summary>
    internal static bool IsPrintable(char c) =>
        c is '\n' or '–' or '—' or '‘' or '’' or '“' or '”' or '…' or '•' or '€'
        || (c >= ' ' && c <= '~')
        || (c >= ' ' && c <= 'ɏ');

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

    /// <summary>
    ///     The blacklist (plan section 1) checked on plain text: lower case and without accents, so
    ///     "paginación" and "paginacion" are the same. A sentence is rejected when it pairs a
    ///     "turn off / remove" verb with something Zentra never turns off. It is a safety net, not
    ///     a guarantee: the action ids are already a closed list.
    /// </summary>
    internal static IEnumerable<string> CheckWords(IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            var plain = Plain(text);
            if (UrlOrEmail().IsMatch(plain))
                yield return "No pongas enlaces, páginas web ni correos.";
            if (AlwaysForbidden().Match(plain) is { Success: true } m)
                yield return $"No menciones \"{m.Value}\": está fuera de lo que ofrece el servicio.";

            foreach (var sentence in SentenceBreak().Split(plain))
            {
                var verb = DisableVerb().Match(sentence);
                if (!verb.Success)
                    continue;
                var target = Protected().Match(sentence);
                if (!target.Success && RemovalVerb().IsMatch(sentence))
                    target = ProtectedFromRemoval().Match(sentence);
                if (target.Success)
                    yield return $"No sugieras \"{verb.Value} … {target.Value}\": el servicio nunca apaga ni quita eso.";
            }
        }
    }

    /// <summary>Lower case, no accents: the form every blacklist pattern is written in.</summary>
    internal static string Plain(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(
        @"\b(hosts|bcdedit|kms|activador(es)?|cracks?|limpiador(es)? del? registro|(liberador|optimizador)(es)? de (la )?(ram|memoria)|ccleaner|advanced systemcare|iobit|driver booster|razer cortex)\b"
    )]
    private static partial Regex AlwaysForbidden();

    [GeneratedRegex(@"\b(desactiv|deshabilit|apag|quit|elimin|desinstal|borr|inhabilit)\w*")]
    private static partial Regex DisableVerb();

    [GeneratedRegex(@"\b(quit|elimin|desinstal|borr)\w*")]
    private static partial Regex RemovalVerb();

    // "Ajuste automático de TCP" is not here: a finding legitimately says it "was turned off".
    [GeneratedRegex(
        @"\b(defender|antivirus|windows update|uac|control de cuentas|bitlocker|smart app control|paginacion|pagefile|auto-?tuning|autoajuste)\b"
    )]
    private static partial Regex Protected();

    // Edge may be "turned off in the background" (a catalog action); only removing it is out.
    [GeneratedRegex(@"\b(edge|webview2?)\b")]
    private static partial Regex ProtectedFromRemoval();

    [GeneratedRegex(@"[.;:!?\n]+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(
        @"https?://|www\.|\b[\w.-]+@[\w-]+\.\w+|\b[\w-]+\.[a-z]{2,}/|\b[\w-]+\.(com|net|org|co|io|es|xyz|ly|me|info|app|dev|ai|gg|tk|ru|cn|us|uk|de|mx|ar|cl|pe|biz|site|online|store|shop|link|top|club|click)\b"
    )]
    private static partial Regex UrlOrEmail();
}
