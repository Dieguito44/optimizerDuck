using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Conditions;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Bloatware;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Test.Services.Profiles;

public class StandardServiceProfileTests
{
    [Fact]
    public void EverySetting_IsASafeOptimization_ListedOnce()
    {
        Assert.Equal(
            StandardServiceProfile.Settings.Count,
            StandardServiceProfile.Settings.Distinct().Count()
        );
        Assert.All(
            StandardServiceProfile.Settings,
            type =>
            {
                Assert.True(typeof(BaseOptimization).IsAssignableFrom(type), type.Name);
                var attribute = type.GetCustomAttribute<OptimizationAttribute>();
                Assert.NotNull(attribute);
                Assert.Equal(OptimizationRisk.Safe, attribute!.Risk);
            }
        );
    }

    [Theory]
    [InlineData(typeof(Storage.DismComponentCleanup))] // irreversible
    [InlineData(typeof(Storage.DismResetBase))] // irreversible, Risky
    [InlineData(typeof(PowerManagement.DisableHibernateAndFastStartup))] // plan section 11
    [InlineData(typeof(AI.DisableRecall))] // Moderate
    [InlineData(typeof(SecurityAndPrivacy.DisableNewsAndInterests))] // blocked by UCPD
    public void TheProfile_LeavesOut(Type excluded)
    {
        Assert.DoesNotContain(excluded, StandardServiceProfile.Settings);
    }

    [Fact]
    public void ThePlanDecisions_AreIn()
    {
        // Diego, 19/11/2026: Location and Find My Device are part of the profile.
        Assert.Contains(
            typeof(SecurityAndPrivacy.DisableLocationAndSensors),
            StandardServiceProfile.Settings
        );
        Assert.Contains(
            typeof(SecurityAndPrivacy.DisableFindMyDevice),
            StandardServiceProfile.Settings
        );
    }

    [Fact]
    public void NoProfileApp_IsAProtectedApp()
    {
        Assert.All(StandardServiceProfile.Apps, a => Assert.False(Shared.IsProtectedApp(a), a));
    }

    [Theory]
    [InlineData("Microsoft.BingNews", true)]
    [InlineData("microsoft.getstarted", true)]
    [InlineData("king.com.CandyCrushSodaSaga", true)]
    [InlineData("A278AB0D.MarchofEmpires", true)]
    [InlineData("Microsoft.BingNewsExtra", false)] // exact names do not match prefixes
    [InlineData("Microsoft.WindowsCalculator", false)]
    [InlineData("Microsoft.GamingApp", false)] // Xbox: ask the client (plan 6.2)
    [InlineData("Microsoft.YourPhone", false)]
    [InlineData("4DF9E0F8.Netflix", false)] // the client may use it
    public void IsProfileApp_MatchesTheList(string package, bool expected)
    {
        Assert.Equal(expected, StandardServiceProfile.IsProfileApp(package));
    }
}

public class ServiceProfileRunnerTests
{
    /// <summary>Real optimization objects, so the runner finds them by type; never applied.</summary>
    private static List<IOptimization> RealSettings() =>
        StandardServiceProfile
            .Settings.Select(t =>
            {
                var o = (BaseOptimization)Activator.CreateInstance(t)!;
                o.OwnerType = t.DeclaringType; // what the registry does on discovery
                return (IOptimization)o;
            })
            .ToList();

    private sealed class FakeOperations(List<IOptimization> all) : IProfileOperations
    {
        public HashSet<Type> Applied { get; } = [];
        public HashSet<Type> Blocked { get; } = [];
        public Dictionary<Type, OptimizationSuccessResult> Outcomes { get; } = [];
        public List<AppXPackage> Installed { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<IOptimization>> AllOptimizationsAsync() =>
            Task.FromResult<IReadOnlyList<IOptimization>>(all);

        public Task<bool> IsAppliedAsync(IOptimization o) =>
            Task.FromResult(Applied.Contains(o.GetType()));

        public Task<ConditionResult> EvaluateConditionAsync(IOptimization o) =>
            Task.FromResult(
                Blocked.Contains(o.GetType())
                    ? ConditionResult.Unsupported(() => "t", () => "d")
                    : ConditionResult.Available
            );

        public Task<OptimizationSuccessResult> ApplyAsync(
            IOptimization o,
            IProgress<ProcessingProgress> p
        )
        {
            Calls.Add("apply:" + o.GetType().Name);
            var status = Outcomes.GetValueOrDefault(o.GetType(), OptimizationSuccessResult.Success);
            if (status == OptimizationSuccessResult.Success)
                Applied.Add(o.GetType());
            return Task.FromResult(status);
        }

        public Task<bool> RevertAsync(IOptimization o, IProgress<ProcessingProgress> p)
        {
            Calls.Add("revert:" + o.GetType().Name);
            Applied.Remove(o.GetType());
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<AppXPackage>> InstalledAppsAsync() =>
            Task.FromResult<IReadOnlyList<AppXPackage>>(Installed);

        public Task<bool> RemoveAppAsync(AppXPackage app)
        {
            Calls.Add("remove:" + app.Name);
            return Task.FromResult(true);
        }
    }

    private static AppXPackage App(string name) =>
        new()
        {
            Name = name,
            PackageFullName = name + "_1.0.0.0_x64__abc",
            Publisher = "CN=Test",
            Version = "1.0.0.0",
            InstallLocation = "",
        };

    private static ServiceProfileRunner Runner(FakeOperations ops) =>
        new(ops, NullLogger<ServiceProfileRunner>.Instance);

    private static readonly IProgress<ProcessingProgress> NoProgress =
        new Progress<ProcessingProgress>();

    [Fact]
    public async Task Apply_RunsEverySetting_SkipsBlockedAndApplied_AndRemovesOnlyProfileApps()
    {
        var ops = new FakeOperations(RealSettings());
        ops.Blocked.Add(typeof(AI.DisableClickToDo));
        ops.Applied.Add(typeof(Storage.EnableTrim));
        ops.Outcomes[typeof(Storage.EnableStorageSense)] = OptimizationSuccessResult.NothingToDo;
        ops.Outcomes[typeof(Performance.TcpAutoTuningNormal)] =
            OptimizationSuccessResult.PartialSuccess;
        ops.Installed.AddRange([
            App("Microsoft.BingNews"),
            App("Microsoft.WindowsCalculator"),
            App("Microsoft.GamingApp"),
        ]);

        var result = await Runner(ops).ApplyAsync(true, NoProgress, CancellationToken.None);

        var total = StandardServiceProfile.Settings.Count;
        Assert.Equal(total, result.Settings.Count);
        Assert.Equal(1, result.Count(ProfileStepOutcome.NotApplicable));
        Assert.Equal(1, result.Count(ProfileStepOutcome.AlreadyApplied));
        Assert.Equal(1, result.Count(ProfileStepOutcome.AlreadyOptimal));
        Assert.Equal(1, result.Count(ProfileStepOutcome.Failed));
        Assert.Equal(total - 4, result.Count(ProfileStepOutcome.Applied));
        Assert.DoesNotContain("apply:DisableClickToDo", ops.Calls);
        Assert.DoesNotContain("apply:EnableTrim", ops.Calls);
        Assert.Equal(["Microsoft.BingNews"], result.AppsRemoved);
        Assert.DoesNotContain("remove:Microsoft.WindowsCalculator", ops.Calls);
    }

    [Fact]
    public async Task Revert_UndoesOnlyWhatHasRevertData()
    {
        var ops = new FakeOperations(RealSettings());
        ops.Applied.Add(typeof(SecurityAndPrivacy.DisableActivityHistory));
        ops.Applied.Add(typeof(AI.DisableEdgeAiFeatures));

        var result = await Runner(ops).RevertAsync(NoProgress, CancellationToken.None);

        Assert.Equal(2, result.Count(ProfileStepOutcome.Reverted));
        Assert.Equal(
            StandardServiceProfile.Settings.Count - 2,
            result.Count(ProfileStepOutcome.NothingToRevert)
        );
        Assert.Empty(ops.Applied);
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("remove:"));
    }

    [Fact]
    public async Task ApplyThenRevert_LeavesNothingApplied()
    {
        // The phase goal: "the profile applies and reverts completely".
        var ops = new FakeOperations(RealSettings());

        await Runner(ops).ApplyAsync(false, NoProgress, CancellationToken.None);
        Assert.Equal(StandardServiceProfile.Settings.Count, ops.Applied.Count);

        await Runner(ops).RevertAsync(NoProgress, CancellationToken.None);
        Assert.Empty(ops.Applied);
    }
}
