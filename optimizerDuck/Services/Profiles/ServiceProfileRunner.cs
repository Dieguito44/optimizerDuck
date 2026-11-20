using Microsoft.Extensions.Logging;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Conditions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Bloatware;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Conditions;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Services.Profiles;

/// <summary>What happened to one setting of the profile.</summary>
public enum ProfileStepOutcome
{
    Applied,
    AlreadyOptimal,
    AlreadyApplied,
    NotApplicable,
    Failed,
    Reverted,
    NothingToRevert,
}

public sealed record ProfileStepResult(string Name, ProfileStepOutcome Outcome);

public sealed record ProfileRunResult(
    IReadOnlyList<ProfileStepResult> Settings,
    IReadOnlyList<string> AppsRemoved,
    IReadOnlyList<string> AppsFailed
)
{
    public int Count(ProfileStepOutcome outcome) => Settings.Count(s => s.Outcome == outcome);
}

/// <summary>
///     The operations the profile needs, so the runner can be tested without touching Windows.
///     The real ones go through <see cref="OptimizationService" /> (the same path as the Optimize
///     page: revert data, history, retries) and <see cref="BloatwareService" />.
/// </summary>
public interface IProfileOperations
{
    Task<IReadOnlyList<IOptimization>> AllOptimizationsAsync();
    Task<bool> IsAppliedAsync(IOptimization optimization);
    Task<ConditionResult> EvaluateConditionAsync(IOptimization optimization);
    Task<OptimizationSuccessResult> ApplyAsync(
        IOptimization optimization,
        IProgress<ProcessingProgress> progress
    );
    Task<bool> RevertAsync(IOptimization optimization, IProgress<ProcessingProgress> progress);
    Task<IReadOnlyList<AppXPackage>> InstalledAppsAsync();
    Task<bool> RemoveAppAsync(AppXPackage app);

    /// <summary>Runs the optimization as a dry run and returns what it would change (plan F2-03).</summary>
    Task<IReadOnlyList<PlannedChange>> PreviewAsync(IOptimization optimization);
}

/// <summary>
///     Applies and reverts the "Servicio estándar" profile. The restore point and the
///     confirmation are the page's job; this class only runs the closed list.
/// </summary>
public sealed class ServiceProfileRunner(
    IProfileOperations operations,
    ILogger<ServiceProfileRunner> logger
)
{
    /// <summary>Every optimization this Zentra has: what a preset file is validated against.</summary>
    public Task<IReadOnlyList<IOptimization>> CatalogAsync() => operations.AllOptimizationsAsync();

    /// <summary>The standard profile's settings, resolved against the loaded optimizations.</summary>
    public Task<IReadOnlyList<IOptimization>> SettingsAsync() =>
        SettingsAsync(ServicePreset.Standard);

    /// <summary>A preset's settings, resolved by GUID against the loaded optimizations, in order.</summary>
    public async Task<IReadOnlyList<IOptimization>> SettingsAsync(ServicePreset preset)
    {
        var all = await operations.AllOptimizationsAsync();
        return preset
            .Settings.Select(id => all.FirstOrDefault(o => o.Id == id))
            .OfType<IOptimization>()
            .ToList();
    }

    public Task<IReadOnlyList<PlannedChange>> PreviewAsync(CancellationToken cancellationToken) =>
        PreviewAsync(ServicePreset.Standard, cancellationToken);

    /// <summary>
    ///     What the profile would change on this PC, value by value (plan F2-03, "WhatIf"): the
    ///     same settings, skipped the same way as <see cref="ApplyAsync" />, run as a dry run.
    ///     Nothing is written and nothing is recorded.
    /// </summary>
    public async Task<IReadOnlyList<PlannedChange>> PreviewAsync(
        ServicePreset preset,
        CancellationToken cancellationToken
    )
    {
        var plan = new List<PlannedChange>();
        foreach (var optimization in await SettingsAsync(preset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((await operations.EvaluateConditionAsync(optimization)).IsBlocking)
                continue;
            if (await operations.IsAppliedAsync(optimization))
                continue;
            plan.AddRange(await operations.PreviewAsync(optimization));
        }
        return plan;
    }

    /// <summary>The installed apps the standard profile would remove.</summary>
    public Task<IReadOnlyList<AppXPackage>> AppsToRemoveAsync() =>
        AppsToRemoveAsync(ServicePreset.Standard);

    /// <summary>The installed apps a preset would remove. Protected apps never are.</summary>
    public async Task<IReadOnlyList<AppXPackage>> AppsToRemoveAsync(ServicePreset preset) =>
        (await operations.InstalledAppsAsync()).Where(a => preset.RemovesApp(a.Name)).ToList();

    public Task<ProfileRunResult> ApplyAsync(
        bool removeApps,
        IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken
    ) => ApplyAsync(ServicePreset.Standard, removeApps, progress, cancellationToken);

    public async Task<ProfileRunResult> ApplyAsync(
        ServicePreset preset,
        bool removeApps,
        IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken
    )
    {
        var settings = await SettingsAsync(preset);
        var apps = removeApps ? await AppsToRemoveAsync(preset) : [];
        var total = settings.Count + apps.Count;
        var step = 0;
        var results = new List<ProfileStepResult>();

        foreach (var optimization in settings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, ++step, total, optimization.Name);

            if ((await operations.EvaluateConditionAsync(optimization)).IsBlocking)
            {
                results.Add(new(optimization.Name, ProfileStepOutcome.NotApplicable));
                continue;
            }

            if (await operations.IsAppliedAsync(optimization))
            {
                results.Add(new(optimization.Name, ProfileStepOutcome.AlreadyApplied));
                continue;
            }

            var status = await operations.ApplyAsync(optimization, Silent);
            results.Add(
                new(
                    optimization.Name,
                    status switch
                    {
                        OptimizationSuccessResult.Success => ProfileStepOutcome.Applied,
                        OptimizationSuccessResult.NothingToDo => ProfileStepOutcome.AlreadyOptimal,
                        // Partial: some steps changed and saved their revert data; the rest
                        // failed. Counted as failed so the technician looks at it.
                        _ => ProfileStepOutcome.Failed,
                    }
                )
            );
        }

        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, ++step, total, app.Name);
            if (await operations.RemoveAppAsync(app))
                removed.Add(app.Name);
            else
                failed.Add(app.Name);
        }

        var result = new ProfileRunResult(results, removed, failed);
        logger.LogInformation(
            "Preset {Preset} applied: {Applied} applied, {Optimal} already optimal, {Already} already applied, {NA} not applicable, {Failed} failed; {Removed} apps removed, {AppsFailed} apps failed",
            preset.Name,
            result.Count(ProfileStepOutcome.Applied),
            result.Count(ProfileStepOutcome.AlreadyOptimal),
            result.Count(ProfileStepOutcome.AlreadyApplied),
            result.Count(ProfileStepOutcome.NotApplicable),
            result.Count(ProfileStepOutcome.Failed),
            removed.Count,
            failed.Count
        );
        return result;
    }

    /// <summary>
    ///     Reverts every setting of the profile that has revert data, whoever applied it. Removed
    ///     apps do not come back: they are reinstalled from the Microsoft Store.
    /// </summary>
    public Task<ProfileRunResult> RevertAsync(
        IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken
    ) => RevertAsync(ServicePreset.Standard, progress, cancellationToken);

    public async Task<ProfileRunResult> RevertAsync(
        ServicePreset preset,
        IProgress<ProcessingProgress> progress,
        CancellationToken cancellationToken
    )
    {
        var settings = await SettingsAsync(preset);
        var results = new List<ProfileStepResult>();
        var step = 0;

        foreach (var optimization in settings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, ++step, settings.Count, optimization.Name);

            if (!await operations.IsAppliedAsync(optimization))
            {
                results.Add(new(optimization.Name, ProfileStepOutcome.NothingToRevert));
                continue;
            }

            var ok = await operations.RevertAsync(optimization, Silent);
            results.Add(
                new(optimization.Name, ok ? ProfileStepOutcome.Reverted : ProfileStepOutcome.Failed)
            );
        }

        var result = new ProfileRunResult(results, [], []);
        logger.LogInformation(
            "Standard profile reverted: {Reverted} reverted, {Nothing} had nothing to revert, {Failed} failed",
            result.Count(ProfileStepOutcome.Reverted),
            result.Count(ProfileStepOutcome.NothingToRevert),
            result.Count(ProfileStepOutcome.Failed)
        );
        return result;
    }

    private static readonly IProgress<ProcessingProgress> Silent =
        new Progress<ProcessingProgress>();

    private static void Report(
        IProgress<ProcessingProgress> progress,
        int value,
        int total,
        string name
    ) =>
        progress.Report(
            new ProcessingProgress
            {
                Message = Configuration.Loc.Instance["Profile.Progress", value, total, name],
                Value = value,
                Total = total,
            }
        );
}

/// <summary>The real operations: the Optimize page's services.</summary>
public sealed class ProfileOperations(
    OptimizationRegistry registry,
    OptimizationService optimizationService,
    BloatwareService bloatwareService,
    System.SystemInfoService systemInfoService,
    System.Primitives.ShellService shell,
    System.StreamService stream,
    System.PowerPlanService powerPlans,
    ILogger<ProfileOperations> logger
) : IProfileOperations
{
    public async Task<IReadOnlyList<IOptimization>> AllOptimizationsAsync()
    {
        await registry.EnsurePreloadedAsync();
        return registry.OptimizationCategories.SelectMany(c => c.Optimizations).ToList();
    }

    public async Task<bool> IsAppliedAsync(IOptimization optimization)
    {
        await OptimizationService.UpdateOptimizationStateAsync(optimization);
        return optimization.State.IsApplied;
    }

    public async Task<ConditionResult> EvaluateConditionAsync(IOptimization optimization)
    {
        var snapshot = await systemInfoService.EnsureSnapshotAsync();
        var result = ConditionEvaluator.Evaluate(optimization.ConditionType, snapshot, logger);
        optimization.ConditionResult = result;
        return result;
    }

    public async Task<OptimizationSuccessResult> ApplyAsync(
        IOptimization optimization,
        IProgress<ProcessingProgress> progress
    )
    {
        var result = await optimizationService.ApplyAsync(optimization, progress);
        await OptimizationService.UpdateOptimizationStateAsync(optimization);
        return result.Status;
    }

    public async Task<bool> RevertAsync(
        IOptimization optimization,
        IProgress<ProcessingProgress> progress
    )
    {
        var result = await optimizationService.RevertAsync(optimization, progress);
        await OptimizationService.UpdateOptimizationStateAsync(optimization);
        return result.Success;
    }

    public async Task<IReadOnlyList<AppXPackage>> InstalledAppsAsync() =>
        await bloatwareService.GetAppXPackagesAsync();

    public async Task<bool> RemoveAppAsync(AppXPackage app) =>
        (await bloatwareService.RemoveAppXPackage(app)).Failures.Count == 0;

    /// <summary>
    ///     The optimization's own ApplyAsync with a <see cref="PreviewPlan" /> in its context:
    ///     the registry, services, tasks, policy and shell primitives only describe their changes.
    ///     Nothing goes through <see cref="OptimizationService" />, so nothing is recorded or saved
    ///     for revert. A setting that fails while previewing just shows no changes.
    /// </summary>
    public async Task<IReadOnlyList<PlannedChange>> PreviewAsync(IOptimization optimization)
    {
        var plan = new PreviewPlan { CurrentSetting = optimization.Name };
        var context = new OptimizationContext
        {
            Snapshot = await systemInfoService.EnsureSnapshotAsync(),
            StreamService = stream,
            Shell = shell,
            PowerPlans = powerPlans,
            Logger = logger,
            Preview = plan,
        };
        try
        {
            await optimization.ApplyAsync(new Progress<ProcessingProgress>(), context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Preview of {Setting} failed", optimization.Name);
        }
        return plan.Items;
    }
}
