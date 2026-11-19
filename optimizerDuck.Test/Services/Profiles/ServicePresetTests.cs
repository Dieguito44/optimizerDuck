using System.Reflection;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>Plan of improvements F3-01: preset files, and what Zentra refuses to import.</summary>
public class ServicePresetTests
{
    /// <summary>Every optimization Zentra has, created as the registry does. Never applied.</summary>
    private static readonly IReadOnlyList<IOptimization> Catalog = typeof(BaseOptimization)
        .Assembly.GetTypes()
        .Where(t =>
            !t.IsAbstract
            && typeof(BaseOptimization).IsAssignableFrom(t)
            && t.GetCustomAttribute<OptimizationAttribute>() is not null
        )
        .Select(t =>
        {
            var o = (BaseOptimization)Activator.CreateInstance(t)!;
            o.OwnerType = t.DeclaringType;
            return (IOptimization)o;
        })
        .ToList();

    private static IOptimization FirstWith(OptimizationRisk risk) =>
        Catalog.First(o => o.Risk == risk);

    private static string Json(
        string settings = "[]",
        string apps = "[]",
        string fragments = "[]",
        string name = "\"Gamer\"",
        bool allowRisky = false,
        int schema = 1
    ) =>
        $$"""
            {
              "schema": {{schema}},
              "name": {{name}},
              "zentraVersion": "1.0.0",
              "created": "2026-11-19",
              "allowRisky": {{(allowRisky ? "true" : "false")}},
              "settings": {{settings}},
              "apps": {{apps}},
              "appFragments": {{fragments}}
            }
            """;

    private static IReadOnlyList<PresetProblemKind> Problems(string json) =>
        ServicePresetFile.Parse(json, Catalog).Problems.Select(p => p.Kind).ToList();

    [Fact]
    public void Standard_IsTheProfileClosedList()
    {
        var standard = ServicePreset.Standard;
        Assert.True(standard.IsBuiltIn);
        Assert.Equal(StandardServiceProfile.Settings.Count, standard.Settings.Count);
        Assert.All(standard.Settings, id => Assert.Contains(Catalog, o => o.Id == id));
        Assert.Equal(StandardServiceProfile.Apps, standard.Apps);
    }

    [Fact]
    public void Standard_ExportsAndImportsBackTheSame()
    {
        var json = ServicePresetFile.Serialize(ServicePreset.Standard);
        var result = ServicePresetFile.Parse(json, Catalog);

        Assert.True(result.IsValid, string.Join(", ", result.Problems));
        Assert.False(result.Preset!.IsBuiltIn);
        Assert.Equal(ServicePreset.Standard.Name, result.Preset.Name);
        Assert.Equal(ServicePreset.Standard.Settings, result.Preset.Settings);
        Assert.Equal(ServicePreset.Standard.Apps, result.Preset.Apps);
        Assert.Equal(ServicePreset.Standard.AppFragments, result.Preset.AppFragments);
        Assert.DoesNotContain("isBuiltIn", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModerateSettings_AreAllowed()
    {
        var id = FirstWith(OptimizationRisk.Moderate).Id;
        Assert.Empty(Problems(Json(settings: $"[\"{id}\"]")));
    }

    [Fact]
    public void RiskySettings_NeedAllowRisky()
    {
        var id = FirstWith(OptimizationRisk.Risky).Id;
        Assert.Equal([PresetProblemKind.RiskyNotAllowed], Problems(Json(settings: $"[\"{id}\"]")));
        Assert.Empty(Problems(Json(settings: $"[\"{id}\"]", allowRisky: true)));
    }

    [Fact]
    public void UnknownAndRepeatedSettings_AreAllReported()
    {
        var known = ServicePreset.Standard.Settings[0];
        var problems = Problems(Json(settings: $"[\"{known}\", \"{Guid.Empty}\", \"{known}\"]"));
        Assert.Equal(
            [PresetProblemKind.UnknownSetting, PresetProblemKind.DuplicateSetting],
            problems.Order()
        );
    }

    [Theory]
    [InlineData("[\"Microsoft.WindowsStore\"]", "[]", PresetProblemKind.ProtectedApp)]
    [InlineData("[\"Microsoft.DesktopAppInstaller\"]", "[]", PresetProblemKind.ProtectedApp)]
    [InlineData("[\"..\\\\evil\"]", "[]", PresetProblemKind.InvalidAppName)]
    [InlineData("[\"a b\"]", "[]", PresetProblemKind.InvalidAppName)]
    [InlineData("[]", "[\"Game\"]", PresetProblemKind.InvalidAppName)] // too short to be a fragment
    [InlineData(
        "[\"Microsoft.BingNews\", \"microsoft.bingnews\"]",
        "[]",
        PresetProblemKind.DuplicateApp
    )]
    public void BadApps_AreRefused(string apps, string fragments, PresetProblemKind expected)
    {
        Assert.Contains(expected, Problems(Json(apps: apps, fragments: fragments)));
    }

    [Theory]
    [InlineData("{ \"schema\": 1, \"name\": \"x\", \"settings\": [], \"script\": \"rm -rf\" }")] // unknown field
    [InlineData("{ \"schema\": 1, \"settings\": [] }")] // no name
    [InlineData("not json")]
    [InlineData("{ \"schema\": 1, \"name\": \"x\", \"settings\": [\"not-a-guid\"] }")]
    public void NotAPreset_IsRefused(string json)
    {
        Assert.Equal([PresetProblemKind.NotJson], Problems(json));
    }

    [Fact]
    public void OtherSchemas_EmptyPresetsAndNoName_AreRefused()
    {
        Assert.Equal([PresetProblemKind.UnsupportedSchema], Problems(Json(schema: 2)));
        Assert.Contains(PresetProblemKind.Empty, Problems(Json()));
        Assert.Contains(
            PresetProblemKind.MissingName,
            Problems(Json(name: "\"   \"", apps: "[\"Microsoft.BingNews\"]"))
        );
    }

    [Fact]
    public void HugeFiles_AreRefusedBeforeParsing()
    {
        var json = Json(apps: "[\"" + new string('a', ServicePresetFile.MaxBytes) + "\"]");
        Assert.Equal([PresetProblemKind.TooLarge], Problems(json));
    }

    [Theory]
    [InlineData("Microsoft.BingNews", true)]
    [InlineData("king.com.CandyCrushSaga", true)] // fragment
    [InlineData("Microsoft.WindowsCalculator", false)]
    public void Standard_RemovesOnlyItsApps(string package, bool removed)
    {
        Assert.Equal(removed, ServicePreset.Standard.RemovesApp(package));
    }

    [Fact]
    public void ProtectedApps_AreNeverRemoved_EvenIfAFragmentMatches()
    {
        var preset = ServicePreset.Standard with { AppFragments = ["WindowsStore"] };
        Assert.False(preset.RemovesApp("Microsoft.WindowsStore"));
    }
}
