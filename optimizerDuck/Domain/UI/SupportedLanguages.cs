using System.Globalization;

namespace optimizerDuck.Domain.UI;

/// <summary>Single source of truth for supported UI languages.</summary>
public static class SupportedLanguages
{
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new() { DisplayName = "English", Culture = new CultureInfo("en-US") },
        new() { DisplayName = "Deutsch", Culture = new CultureInfo("de-DE") },
        new() { DisplayName = "Français", Culture = new CultureInfo("fr-FR") },
        new() { DisplayName = "Tiếng Việt", Culture = new CultureInfo("vi-VN") },
        new() { DisplayName = "正體中文", Culture = new CultureInfo("zh-TW") },
        new() { DisplayName = "简体中文", Culture = new CultureInfo("zh-CN") },
        new() { DisplayName = "Русский", Culture = new CultureInfo("ru-RU") },
        new() { DisplayName = "한국어", Culture = new CultureInfo("ko-KR") },
        new() { DisplayName = "日本語", Culture = new CultureInfo("ja-JP") },
        new() { DisplayName = "Polski", Culture = new CultureInfo("pl-PL") },
        // Plan of improvements F3-11: Colombian Spanish is Zentra's base. The texts are neutral
        // Spanish (Translations.es.resx), so es-ES, es-MX... read them too; es-CO sets the dates
        // and numbers.
        new() { DisplayName = "Español", Culture = new CultureInfo("es-CO") },
        new() { DisplayName = "Português (BR)", Culture = new CultureInfo("pt-BR") },
        new() { DisplayName = "Türkçe", Culture = new CultureInfo("tr-TR") },
        new() { DisplayName = "עברית", Culture = new CultureInfo("he-IL") },
        new() { DisplayName = "العربية", Culture = new CultureInfo("ar-SA") },
        new() { DisplayName = "Italiano", Culture = new CultureInfo("it-IT") },
        new() { DisplayName = "Bahasa Indonesia", Culture = new CultureInfo("id-ID") },
        new() { DisplayName = "فارسی", Culture = new CultureInfo("fa-IR") },
        new() { DisplayName = "Magyar", Culture = new CultureInfo("hu-HU") },
        new() { DisplayName = "Українська", Culture = new CultureInfo("uk-UA") },
    ];

    public static LanguageOption Default => All[0];

    /// <summary>
    ///     The supported language for a saved name: the same culture, or else one of the same
    ///     language ("es-ES", saved before F3-11, becomes "es-CO"), or else <see cref="Default" />.
    /// </summary>
    public static LanguageOption Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Default;
        var exact = All.FirstOrDefault(l =>
            string.Equals(l.Culture.Name, name, StringComparison.OrdinalIgnoreCase)
        );
        if (exact is not null)
            return exact;
        var language = name.Split('-')[0];
        return All.FirstOrDefault(l =>
                string.Equals(
                    l.Culture.TwoLetterISOLanguageName,
                    language,
                    StringComparison.OrdinalIgnoreCase
                )
            ) ?? Default;
    }
}
