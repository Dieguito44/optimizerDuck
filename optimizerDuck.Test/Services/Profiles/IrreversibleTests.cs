using System.Reflection;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Cli;
using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>Plan of improvements F1-09: what cannot be undone asks first, everywhere.</summary>
public class IrreversibleTests
{
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

    [Fact]
    public void ResetBase_IsIrreversible_AndCompactOs_HardToRevert()
    {
        Assert.Equal(
            OptimizationConfirmation.Irreversible,
            new Storage.DismResetBase().Confirmation
        );
        Assert.Equal(
            OptimizationConfirmation.HardToRevert,
            new Storage.EnableCompactOs().Confirmation
        );
    }

    [Fact]
    public void TheStandardProfile_HoldsNothingThatNeedsConfirmation()
    {
        var ids = ServicePreset.Standard.Settings.ToHashSet();
        Assert.All(
            Catalog.Where(o => ids.Contains(o.Id)),
            o => Assert.Equal(OptimizationConfirmation.None, o.Confirmation)
        );
    }

    [Theory]
    [InlineData(typeof(Storage.DismResetBase))]
    [InlineData(typeof(Storage.EnableCompactOs))]
    public void APreset_CannotCarryThem_EvenWithAllowRisky(Type type)
    {
        var id = Catalog.Single(o => o.GetType() == type).Id;
        var json = $$"""
            {
              "schema": 1,
              "name": "Todo",
              "zentraVersion": "1.0.0",
              "created": "2026-11-19",
              "allowRisky": true,
              "settings": ["{{id}}"],
              "apps": [],
              "appFragments": []
            }
            """;

        var problems = ServicePresetFile.Parse(json, Catalog).Problems.Select(p => p.Kind);

        Assert.Contains(PresetProblemKind.NeedsConfirmation, problems);
    }

    [Fact]
    public void TheCli_KeepsTheApps_UnlessToldToRemoveThem()
    {
        var plain = CliOptions.Parse(["--preset", "estandar"], out _)!;
        Assert.False(plain.RemovesApps);

        var remove = CliOptions.Parse(["--preset", "estandar", "--remove-apps"], out _)!;
        Assert.True(remove.RemovesApps);

        Assert.Null(
            CliOptions.Parse(["--preset", "estandar", "--remove-apps", "--keep-apps"], out var both)
        );
        Assert.False(string.IsNullOrEmpty(both));
        Assert.Null(CliOptions.Parse(["--diagnose", "--remove-apps"], out _));
    }
}
