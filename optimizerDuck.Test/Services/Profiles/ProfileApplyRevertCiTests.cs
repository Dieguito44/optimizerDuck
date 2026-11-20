using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Conditions;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.System;
using optimizerDuck.Test.TestDoubles;
using Wpf.Ui;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>
///     Plan of improvements F2-01: every setting of the standard profile is applied for real and
///     reverted, and every value it touches must end exactly as it was. It changes the machine,
///     so it only runs on the disposable GitHub runner (elevated); anywhere else it is skipped.
/// </summary>
/// <remarks>
///     The values are read with the profile preview (F2-03): the dry run reports what each target
///     holds now, so "before" and "after revert" use the same readers the technician sees.
/// </remarks>
public class ProfileApplyRevertCiTests
{
    private static bool OnDisposableRunner =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && Environment.IsPrivilegedProcess;

    [Fact]
    public async Task EveryProfileSetting_AppliesAndRevertsToTheExactPreviousState()
    {
        if (!OnDisposableRunner)
            Assert.Skip(
                "Applies real changes: runs only on the disposable GitHub runner, elevated."
            );

        var snapshot = await new SystemInfoService(
            NullLogger<SystemInfoService>.Instance
        ).RefreshAsync(TestContext.Current.CancellationToken);
        var problems = new List<string>();
        var checkedSettings = 0;

        await RunInStaThreadAsync(async () =>
        {
            var service = CreateService();
            foreach (var type in StandardServiceProfile.Settings)
            {
                var optimization = (BaseOptimization)Activator.CreateInstance(type)!;
                optimization.OwnerType = type.DeclaringType;

                var condition = ConditionEvaluator.Evaluate(
                    optimization.ConditionType,
                    snapshot,
                    NullLogger.Instance
                );
                if (condition.IsBlocking)
                    continue; // not for this Windows (the runner is Windows Server)

                var before = await PreviewAsync(optimization, snapshot);
                if (before.All(c => c.IsAlreadyThere))
                    continue; // already as the profile wants it: nothing to apply or revert

                var revertFile = Path.Combine(Shared.RevertDirectory, optimization.Id + ".json");
                try
                {
                    var applied = await service.ApplyAsync(
                        optimization,
                        new Progress<ProcessingProgress>()
                    );
                    if (applied.Status != OptimizationSuccessResult.Success)
                    {
                        problems.Add($"{type.Name}: apply ended {applied.Status}");
                        continue;
                    }

                    var afterApply = await PreviewAsync(optimization, snapshot);
                    foreach (var c in afterApply.Where(c => IsComparable(c) && !c.IsAlreadyThere))
                        problems.Add(
                            $"{type.Name}: not applied {c.Target} = {c.Current}, wanted {c.Next}"
                        );

                    var reverted = await service.RevertAsync(optimization);
                    if (!reverted.Success)
                    {
                        problems.Add($"{type.Name}: revert failed: {reverted.Message}");
                        continue;
                    }

                    var afterRevert = (await PreviewAsync(optimization, snapshot)).ToDictionary(
                        c => c.Target,
                        StringComparer.OrdinalIgnoreCase
                    );
                    foreach (var c in before.Where(IsComparable))
                    {
                        var now = afterRevert.TryGetValue(c.Target, out var a) ? a.Current : null;
                        if (!string.Equals(c.Current, now, StringComparison.OrdinalIgnoreCase))
                            problems.Add(
                                $"{type.Name}: {c.Target} was {c.Current ?? "(missing)"}, after revert {now ?? "(missing)"}"
                            );
                    }
                    checkedSettings++;
                }
                finally
                {
                    if (File.Exists(revertFile))
                        File.Delete(revertFile);
                }
            }
        });

        Assert.True(checkedSettings > 0, "No profile setting could be checked on this runner.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Commands have no readable "current value"; everything else is compared.</summary>
    private static bool IsComparable(PlannedChange change) =>
        change.Kind != PlannedChangeKind.Command;

    private static async Task<IReadOnlyList<PlannedChange>> PreviewAsync(
        IOptimization optimization,
        SystemInfo snapshot
    )
    {
        var plan = new PreviewPlan { CurrentSetting = optimization.Name };
        var context = new OptimizationContext
        {
            Snapshot = snapshot,
            StreamService = new StreamService(NullLogger<StreamService>.Instance),
            Shell = TestShell.New(),
            PowerPlans = new PowerPlanService(NullLogger<PowerPlanService>.Instance),
            Logger = NullLogger.Instance,
            Preview = plan,
        };
        await optimization.ApplyAsync(new Progress<ProcessingProgress>(), context);
        return plan.Items;
    }

    private static OptimizationService CreateService()
    {
        var revertManager = new RevertManager(
            NullLogger<RevertManager>.Instance,
            TestShell.New(),
            new PowerPlanService(NullLogger<PowerPlanService>.Instance),
            TimeProvider.System
        );
        return new OptimizationService(
            TestRunner.New(revertManager),
            revertManager,
            NullLoggerFactory.Instance,
            new ContentDialogService(),
            new SystemRestoreService(NullLogger<SystemRestoreService>.Instance),
            NullLogger<OptimizationService>.Instance
        );
    }

    private static Task RunInStaThreadAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
