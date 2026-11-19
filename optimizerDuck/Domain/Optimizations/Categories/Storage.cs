using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Conditions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.UI.Pages.Optimize.Categories;

namespace optimizerDuck.Domain.Optimizations.Categories;

/// <summary>
///     Zentra storage category (plan phase 4): component store cleanup, Storage Sense, TRIM,
///     Compact OS and ResetBase. Irreversible steps are recorded as irreversible, never as a
///     change that seems to undo.
/// </summary>
[OptimizationCategory(typeof(StorageOptimizerPage))]
public class Storage : LocalizedObject, IOptimizationCategory
{
    /// <summary>DISM and Compact OS can run for tens of minutes; the app's shell limit is 2.</summary>
    internal const int LongRunTimeoutMs = 60 * 60 * 1000;

    public string Name => Loc.Instance[$"Optimizer.{nameof(Storage)}"];
    public OptimizationCategoryOrder Order { get; init; } = OptimizationCategoryOrder.Storage;
    public ObservableCollection<IOptimization> Optimizations { get; init; } = [];

    /// <summary>A shell whose time limit fits DISM and Compact OS. Same provider, longer limit.</summary>
    internal static ShellService LongRunningShell(ILogger? logger) =>
        new(new ProcessRunner(LongRunTimeoutMs, logger as ILogger<ProcessRunner>));

    /// <summary>
    ///     ACT-DISM-CLEANUP: analyzes the component store, then removes superseded components.
    ///     Irreversible: the removed versions of updates cannot be restored.
    /// </summary>
    [Optimization(
        Id = "3B6C7D0E-9A41-4E2B-8C55-1F0A2D4B6E71",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Disk | OptimizationTags.System
    )]
    public class DismComponentCleanup : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var shell = LongRunningShell(context.Logger);

            progress.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance[$"{ProgressPrefix}.Analyze"],
                    IsIndeterminate = true,
                }
            );
            var analyze = await shell.QueryCMDAsync(
                "dism /Online /Cleanup-Image /AnalyzeComponentStore /English",
                context.Logger,
                ct: context.CancellationToken
            );
            context.Logger.LogInformation(
                "Component store analysis (exit {Code}): {Output}",
                analyze.ExitCode,
                analyze.Stdout
            );

            progress.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance[$"{ProgressPrefix}.Cleanup"],
                    IsIndeterminate = true,
                }
            );
            var cleanup = await shell.QueryCMDAsync(
                "dism /Online /Cleanup-Image /StartComponentCleanup /English",
                context.Logger,
                ct: context.CancellationToken
            );

            RecordIrreversible(context, "DISM /StartComponentCleanup", cleanup);
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>ACT-STORAGESENSE: turns on Storage Sense for the current user. Reverted by registry.</summary>
    [Optimization(
        Id = "6E2F9A41-0C7B-4D38-A915-7B3E5C1D2F84",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Disk | OptimizationTags.System
    )]
    public class EnableStorageSense : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy",
                    "01",
                    1
                )
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     ACT-TRIM: makes sure Windows sends TRIM to SSDs (DisableDeleteNotification = 0, the
    ///     value <c>fsutil behavior set DisableDeleteNotify 0</c> writes). Skipped when already on.
    /// </summary>
    [Optimization(
        Id = "A47D1C93-2E58-4B06-9F3A-C81E6D0B5A27",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Disk | OptimizationTags.Performance
    )]
    public class EnableTrim : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem",
                    "DisableDeleteNotification",
                    0
                )
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     ACT-COMPACTOS: compresses Windows' own files. Only for system disks of 256 GB or less
    ///     (plan section 4). Reverted with <c>compact /CompactOS:never</c>.
    /// </summary>
    [Optimization(
        Id = "C1E85B2A-7F04-4A9D-B36E-2D9F0A71C5E8",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Disk | OptimizationTags.System,
        Condition = typeof(SmallSystemDiskCondition)
    )]
    public class EnableCompactOs : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            progress.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance[$"{ProgressPrefix}.Compressing"],
                    IsIndeterminate = true,
                }
            );

            // The text of "compact /CompactOS:query" is localized, so the state is read from the
            // files: Compact OS stores system files through WOF, which marks them as reparse
            // points. Already compact means a revert must not uncompress what was compact before.
            if (IsCompactOsOn())
            {
                context.Changes.AddSkip(Name, "Compact OS");
                return context.Changes.ToApplyResult();
            }

            await LongRunningShell(context.Logger)
                .CMDAsync("compact /CompactOS:always", context, "compact /CompactOS:never");
            return context.Changes.ToApplyResult();
        }

        /// <summary>Whether Windows' own executables are WOF-compressed (Compact OS on).</summary>
        internal static bool IsCompactOsOn()
        {
            try
            {
                var explorer = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe"
                );
                return System
                    .IO.File.GetAttributes(explorer)
                    .HasFlag(System.IO.FileAttributes.ReparsePoint);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    ///     ACT-DISM-RESETBASE: removes every superseded component. Risky and irreversible: the
    ///     installed updates can no longer be uninstalled. Never part of a default profile.
    /// </summary>
    [Optimization(
        Id = "E9047A6B-3D21-4C8F-A5E0-6B2C9D17F43A",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.Disk | OptimizationTags.System
    )]
    public class DismResetBase : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            progress.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance[$"{ProgressPrefix}.Cleanup"],
                    IsIndeterminate = true,
                }
            );
            var result = await LongRunningShell(context.Logger)
                .QueryCMDAsync(
                    "dism /Online /Cleanup-Image /StartComponentCleanup /ResetBase /English",
                    context.Logger,
                    ct: context.CancellationToken
                );

            RecordIrreversible(context, "DISM /StartComponentCleanup /ResetBase", result);
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Records a command that cannot be undone: irreversible on success, a failed step with
    ///     its output otherwise.
    /// </summary>
    internal static void RecordIrreversible(OpCall call, string command, ShellResult result)
    {
        if (result.ExitCode == 0)
        {
            call.Changes.AddIrreversible(ServiceStrings.ShellName, command);
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;
        call.Changes.Add(
            ServiceStrings.ShellName,
            command,
            false,
            error: Loc.Instance["Optimizer.Storage.Error.CommandFailed", result.ExitCode],
            errorDetail: detail,
            nativeErrorCode: result.ExitCode
        );
    }
}

/// <summary>Compact OS is only worth it on small system disks (plan: 256 GB or less).</summary>
public sealed class SmallSystemDiskCondition : ConditionBase
{
    /// <summary>A "256 GB" disk is 256 000 000 000 bytes; a little margin covers rounding.</summary>
    internal const long MaxBytes = 260_000_000_000;

    public override ConditionResult Evaluate(Services.System.SystemInfo snapshot)
    {
        var system = snapshot.Storage.Volumes.FirstOrDefault(v => v.IsSystemDrive);
        if (system is null || system.TotalBytes <= 0)
            return ConditionResult.Error();

        return system.TotalBytes <= MaxBytes
            ? ConditionResult.Available
            : ConditionResult.Unsupported(
                () => Loc.Instance["Condition.SmallSystemDisk.Title"],
                () => Loc.Instance["Condition.SmallSystemDisk.Description"]
            );
    }
}
