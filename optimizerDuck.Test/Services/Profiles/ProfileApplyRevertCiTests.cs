using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
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

        var problems = new List<string>();
        var checkedSettings = await ApplyAndRevertAsync(
            StandardServiceProfile.Settings,
            problems,
            []
        );

        Assert.True(checkedSettings > 0, "No profile setting could be checked on this runner.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    ///     The same check for every setting Zentra has, not only the profile's, on a real Windows 11:
    ///     only inside Windows Sandbox (ZENTRA_SANDBOX=1, elevated), which is thrown away afterwards.
    ///     Settings behind the "No reversible" dialog are left out. One line per setting goes to
    ///     ZENTRA_SANDBOX_REPORT when set. Added for the version 1 test (20/11/2026).
    /// </summary>
    [Fact]
    public async Task EverySetting_AppliesAndRevertsToTheExactPreviousState_InTheSandbox()
    {
        if (
            Environment.GetEnvironmentVariable("ZENTRA_SANDBOX") != "1"
            || !Environment.IsPrivilegedProcess
        )
            Assert.Skip("Applies every setting for real: only inside Windows Sandbox, elevated.");

        var types = typeof(BaseOptimization)
            .Assembly.GetTypes()
            .Where(t =>
                !t.IsAbstract
                && typeof(BaseOptimization).IsAssignableFrom(t)
                && t.GetCustomAttribute<OptimizationAttribute>() is { } a
                && a.Confirmation == OptimizationConfirmation.None
                && t.DeclaringType?.Name != "Debug" // the Debug category exists only in Debug builds
            )
            .OrderBy(t => t.DeclaringType?.Name)
            .ThenBy(t => t.Name)
            .ToList();
        var problems = new List<string>();
        var report = new List<string>();
        var checkedSettings = await ApplyAndRevertAsync(types, problems, report);

        // GitHub's runner is Windows Server, which Zentra does not serve: what fails only there
        // stays in the report as KNOWN, with its reason, and does not fail the run.
        var snapshot = await new SystemInfoService(
            NullLogger<SystemInfoService>.Instance
        ).EnsureSnapshotAsync(TestContext.Current.CancellationToken);
        if (snapshot.Windows.Edition == WindowsEdition.Server)
            foreach (var p in problems.ToList())
                if (
                    KnownOnWindowsServer.FirstOrDefault(k => p.StartsWith(k.Key + ":")) is
                    { Key: not null } known
                )
                {
                    problems.Remove(p);
                    report.Add($"KNOWN {p[..Math.Min(160, p.Length)]} ({known.Value})");
                }

        if (Environment.GetEnvironmentVariable("ZENTRA_SANDBOX_REPORT") is { Length: > 0 } path)
            File.WriteAllLines(
                path,
                [
                    $"settings: {types.Count}, checked: {checkedSettings}, problems: {problems.Count}",
                    .. report,
                    "--- problems",
                    .. problems,
                ]
            );
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static readonly Dictionary<string, string> KnownOnWindowsServer = new()
    {
        ["WeeklyRestorePoints"] = "Windows Server has no System Restore",
        ["DisablePowerSaving"] =
            "the USB AutomaticSurpriseRemoval key does not exist on Windows Server and its parent is protected; on Windows 11 it exists and administrators can write it",
    };

    private static async Task<int> ApplyAndRevertAsync(
        IEnumerable<Type> types,
        List<string> problems,
        List<string> report
    )
    {
        var snapshot = await new SystemInfoService(
            NullLogger<SystemInfoService>.Instance
        ).RefreshAsync(TestContext.Current.CancellationToken);
        var checkedSettings = 0;

        await RunInStaThreadAsync(async () =>
        {
            var service = CreateService();
            foreach (var type in types)
            {
                var optimization = (BaseOptimization)Activator.CreateInstance(type)!;
                optimization.OwnerType = type.DeclaringType;

                var condition = ConditionEvaluator.Evaluate(
                    optimization.ConditionType,
                    snapshot,
                    NullLogger.Instance
                );
                if (condition.IsBlocking)
                {
                    report.Add($"SKIP {type.Name}: not for this PC ({condition.Title})");
                    continue; // not for this Windows (the runner is Windows Server)
                }

                IReadOnlyList<PlannedChange> before;
                try
                {
                    before = await PreviewAsync(optimization, snapshot);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A preview that throws is a defect of its own, and must not hide the others.
                    problems.Add($"{type.Name}: preview threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (!before.Any(c => c.WillChange))
                {
                    // Already as wanted, or its services and tasks do not exist on this PC.
                    report.Add($"SKIP {type.Name}: nothing to change on this PC");
                    continue;
                }

                var revertFile = Path.Combine(Shared.RevertDirectory, optimization.Id + ".json");
                try
                {
                    var applied = await service.ApplyAsync(
                        optimization,
                        new Progress<ProcessingProgress>()
                    );
                    if (applied.Status == OptimizationSuccessResult.NothingToDo)
                    {
                        // The client's consent (refused when no one is there to answer), a
                        // recent pause, or a disk the command does not support.
                        report.Add($"SKIP {type.Name}: nothing done ({applied.Message})");
                        continue;
                    }
                    if (applied.Status != OptimizationSuccessResult.Success)
                    {
                        problems.Add($"{type.Name}: apply ended {applied.Status}: {Why(applied)}");
                        if (applied.Status == OptimizationSuccessResult.Failed)
                            continue;
                    }

                    if (!File.Exists(revertFile))
                    {
                        // Commands that cannot be undone (a cleanup, a re-trim) leave no revert data.
                        if (before.Where(c => c.WillChange).All(c => !IsComparable(c)))
                            report.Add($"OK   {type.Name}: applied, not reversible by design");
                        else
                            problems.Add($"{type.Name}: applied but saved no revert data");
                        continue;
                    }

                    var afterApply = await PreviewAsync(optimization, snapshot);
                    foreach (var c in afterApply.Where(c => IsComparable(c) && c.WillChange))
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
                    foreach (var c in before.Where(c => IsComparable(c) && !c.TargetMissing))
                    {
                        var now = afterRevert.TryGetValue(c.Target, out var a) ? a.Current : null;
                        if (!string.Equals(c.Current, now, StringComparison.OrdinalIgnoreCase))
                            problems.Add(
                                $"{type.Name}: {c.Target} was {c.Current ?? "(missing)"}, after revert {now ?? "(missing)"}"
                            );
                    }
                    checkedSettings++;
                    report.Add($"OK   {type.Name}: applied and reverted ({before.Count} values)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One setting that throws must not hide the others.
                    problems.Add($"{type.Name}: threw {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (File.Exists(revertFile))
                        File.Delete(revertFile);
                }
            }
        });

        return checkedSettings;
    }

    /// <summary>The result's message plus each failed step, so the report says why.</summary>
    private static string Why(OptimizationResult result) =>
        string.Join(
            " | ",
            new[] { result.Message, result.Exception?.Message }
                .Concat(
                    result.FailedSteps.Select(f =>
                        $"{f.Description}: {f.Error} {f.ErrorDetail} (code {f.NativeErrorCode})"
                    )
                )
                .Where(m => !string.IsNullOrWhiteSpace(m))
        );

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
