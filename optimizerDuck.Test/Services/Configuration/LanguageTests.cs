using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using optimizerDuck.Domain.UI;
using optimizerDuck.Resources.Languages;

namespace optimizerDuck.Test.Services.Configuration;

/// <summary>
///     Plan of improvements F3-11: the languages inherited from optimizerDuck stay alive, and
///     Colombian Spanish is the base. Every language has every key, the placeholders match, and
///     every literal key the code uses exists.
/// </summary>
public partial class LanguageTests
{
    private static string LanguagesDir() => Path.Combine(SourceDir(), "Resources", "Languages");

    private static Dictionary<string, string> Load(string file) =>
        XDocument
            .Load(file)
            .Root!.Elements("data")
            .ToDictionary(
                e => (string)e.Attribute("name")!,
                e => (string?)e.Element("value") ?? ""
            );

    private static Dictionary<string, string> Neutral() =>
        Load(Path.Combine(LanguagesDir(), "Translations.resx"));

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void EveryLanguage_HasEveryKey_AndNoUnknownPlaceholder()
    {
        var neutral = Neutral();
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(LanguagesDir(), "Translations.*.resx"))
        {
            var translated = Load(file);
            var name = Path.GetFileName(file);
            foreach (var (key, text) in neutral)
            {
                if (!translated.TryGetValue(key, out var value))
                    problems.Add($"{name}: missing {key}");
                // A placeholder the English text does not have would throw at runtime.
                else if (!Placeholders(value).IsSubsetOf(Placeholders(text)))
                    problems.Add($"{name}: {key} has placeholders English lacks");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    [Fact]
    public void EverySupportedLanguage_HasItsFile_AndEveryFileIsSupported()
    {
        var files = Directory
            .GetFiles(LanguagesDir(), "Translations.*.resx")
            .Select(f => Path.GetFileNameWithoutExtension(f)["Translations.".Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var language in SupportedLanguages.All)
        {
            var culture = language.Culture;
            if (culture.TwoLetterISOLanguageName == "en")
                continue; // the neutral file
            Assert.True(
                files.Contains(culture.Name) || files.Contains(culture.TwoLetterISOLanguageName),
                $"{culture.Name} has no Translations file"
            );
        }
        foreach (var file in files)
            Assert.Contains(
                SupportedLanguages.All,
                l =>
                    string.Equals(l.Culture.Name, file, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        l.Culture.TwoLetterISOLanguageName,
                        file,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
    }

    /// <summary>A dialog inherited from optimizerDuck that nothing opens: its keys never existed.</summary>
    private const string UnusedUpstreamDialog = "ScheduledTaskCreateDialog.xaml";

    [GeneratedRegex(@"Loc\.Instance\[\s*""(?<k>[^""{}]+)""|\{ext:Loc\s+(?<k>[A-Za-z0-9_.\-]+)\}")]
    private static partial Regex LiteralKey();

    [Fact]
    public void EveryLiteralKeyInTheCode_Exists()
    {
        var neutral = Neutral();
        var missing = new List<string>();
        foreach (
            var file in Directory
                .EnumerateFiles(SourceDir(), "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
                .Where(f =>
                    !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                )
                .Where(f =>
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                )
                .Where(f => Path.GetFileName(f) != UnusedUpstreamDialog)
        )
        foreach (Match m in LiteralKey().Matches(File.ReadAllText(file)))
        {
            var key = m.Groups["k"].Value;
            // Examples inside documentation comments ("Key", "X.Y.Name").
            if (!key.Contains('.') || key == "X.Y.Name")
                continue;
            if (!neutral.ContainsKey(key))
                missing.Add($"{Path.GetFileName(file)}: {key}");
        }
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing.Distinct()));
    }

    [Theory]
    [InlineData("es-CO", "es-CO")]
    [InlineData("es-ES", "es-CO")] // saved before F3-11
    [InlineData("es-MX", "es-CO")]
    [InlineData("fr-CA", "fr-FR")]
    [InlineData("en-US", "en-US")]
    [InlineData("xx-YY", "en-US")]
    [InlineData(null, "en-US")]
    public void SavedLanguages_Resolve(string? saved, string expected)
    {
        Assert.Equal(expected, SupportedLanguages.Resolve(saved).Culture.Name);
    }

    [Theory]
    [InlineData("es-CO")]
    [InlineData("es-ES")]
    [InlineData("es-MX")]
    public void EverySpanish_ReadsTheSpanishTexts(string culture)
    {
        // A fresh manager: the shared one caches what it finds per culture, which would hide
        // a missing satellite from the other language tests.
        var resources = new global::System.Resources.ResourceManager(
            "optimizerDuck.Resources.Languages.Translations",
            typeof(Translations).Assembly
        );
        Assert.Equal(
            "Importar preset",
            resources.GetString("Preset.Button.Import", new CultureInfo(culture))
        );
    }

    [Fact]
    public void ZentraStarts_InColombianSpanish()
    {
        Assert.Equal("es-CO", new optimizerDuck.Domain.Configuration.AppSettings().App.Language);
    }

    private static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "optimizerDuck", "optimizerDuck.csproj"))
        )
            dir = dir.Parent;
        return dir is null
            ? throw new DirectoryNotFoundException("Could not locate the app source directory.")
            : Path.Combine(dir.FullName, "optimizerDuck");
    }
}
