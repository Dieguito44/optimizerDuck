using System.Reflection;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Profiles;
using optimizerDuck.Services.System;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>
///     Version 1.1.0 (20/11/2026): the profiles besides the "Servicio estándar", the suggestion for
///     the PC, and the Experto tab Diego took out of the blacklist.
/// </summary>
public class ServiceProfilesTests
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

    public static TheoryData<string> BuiltInNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in ServicePreset.BuiltIns)
            data.Add(p.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInNames))]
    public void EveryProfile_IsTheStandardOnePlusOnlySafeOrModerateSettings_WithoutConfirmation(
        string name
    )
    {
        var preset = ServicePreset.BuiltIns.Single(p => p.Name == name);

        Assert.True(preset.IsBuiltIn);
        Assert.False(preset.AllowRisky);
        Assert.False(string.IsNullOrWhiteSpace(preset.DescriptionKey));
        Assert.All(ServicePreset.Standard.Settings, id => Assert.Contains(id, preset.Settings));
        Assert.Equal(preset.Settings.Count, preset.Settings.Distinct().Count());
        Assert.Equal(ServicePreset.Standard.Apps, preset.Apps);
        foreach (var id in preset.Settings)
        {
            var setting = Assert.Single(Catalog, o => o.Id == id);
            Assert.NotEqual(OptimizationRisk.Risky, setting.Risk);
            Assert.Equal(OptimizationConfirmation.None, setting.Confirmation);
            Assert.NotEqual(typeof(Expert), ((BaseOptimization)setting).OwnerType);
        }
    }

    [Fact]
    public void TheProfilesAreDifferent()
    {
        Assert.Equal(4, ServicePreset.BuiltIns.Count);
        Assert.Equal(4, ServicePreset.BuiltIns.Select(p => p.Name).Distinct().Count());
        Assert.True(ServicePreset.SlowPc.Settings.Count > ServicePreset.Standard.Settings.Count);
        Assert.True(ServicePreset.Gaming.Settings.Count > ServicePreset.Standard.Settings.Count);
        Assert.True(ServicePreset.Privacy.Settings.Count > ServicePreset.Standard.Settings.Count);
    }

    [Fact]
    public void ExpertTab_IsAllRisky_AndHasTheSixSettingsDiegoApproved()
    {
        var expert = Catalog.Where(o => ((BaseOptimization)o).OwnerType == typeof(Expert)).ToList();

        Assert.Equal(6, expert.Count);
        Assert.All(expert, o => Assert.Equal(OptimizationRisk.Risky, o.Risk));
        // The two that cannot be undone from Zentra ask first.
        Assert.Equal(
            OptimizationConfirmation.Irreversible,
            Catalog.Single(o => o is Expert.RemoveEdge).Confirmation
        );
        Assert.Equal(
            OptimizationConfirmation.HardToRevert,
            Catalog.Single(o => o is Expert.DisableBitLocker).Confirmation
        );
    }

    private static SystemInfo Pc(StorageMediaType disk, double ramGb) =>
        SystemInfo.Unknown with
        {
            Storage = new StorageInfo
            {
                Volumes = [new StorageVolume { DriveLetter = "C:", IsSystemDrive = true, MediaType = disk }],
            },
            Memory = SystemInfo.Unknown.Memory with { TotalBytes = (long)(ramGb * 1024 * 1024 * 1024) },
        };

    [Fact]
    public void Suggest_SlowPc_ForAMechanicalDisk()
    {
        var (preset, reason) = ServiceProfiles.Suggest(Pc(StorageMediaType.Hdd, 16), ["D:\\Steam"]);
        Assert.Same(ServicePreset.SlowPc, preset);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Suggest_SlowPc_For8GbOrLess() =>
        Assert.Same(ServicePreset.SlowPc, ServiceProfiles.Suggest(Pc(StorageMediaType.Ssd, 7.9), []).Preset);

    [Fact]
    public void Suggest_Gaming_WhenThereAreGames() =>
        Assert.Same(ServicePreset.Gaming, ServiceProfiles.Suggest(Pc(StorageMediaType.Nvme, 16), ["D:\\Steam"]).Preset);

    [Fact]
    public void Suggest_Standard_Otherwise() =>
        Assert.Same(ServicePreset.Standard, ServiceProfiles.Suggest(Pc(StorageMediaType.Nvme, 16), []).Preset);
}
