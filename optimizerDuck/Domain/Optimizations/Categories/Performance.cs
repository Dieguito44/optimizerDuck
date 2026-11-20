using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.UI.Pages.Optimize.Categories;

namespace optimizerDuck.Domain.Optimizations.Categories;

[OptimizationCategory(typeof(PerformanceOptimizerPage))]
public class Performance : LocalizedObject, IOptimizationCategory
{
    public string Name => Loc.Instance[$"Optimizer.{nameof(Performance)}"];
    public OptimizationCategoryOrder Order { get; init; } = OptimizationCategoryOrder.Performance;
    public ObservableCollection<IOptimization> Optimizations { get; init; } = [];

    [Optimization(
        Id = "648EC19A-FDA5-4607-8A7C-148B8B05FB4C",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.System | OptimizationTags.Performance | OptimizationTags.Ram
    )]
    public class DisableBackgroundApps : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
                    "GlobalUserDisabled",
                    1
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                    "BackgroundAppGlobalToggle",
                    0
                )
            );
            context.Logger.LogInformation("Disabled background apps");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "CD436A05-51F1-46E9-B4DE-5262EE7F812A",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.System | OptimizationTags.Performance | OptimizationTags.Ram
    )]
    public class ConsolidateServiceHosts : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            if (context.Snapshot.Memory.TotalKB <= 0)
            {
                context.Logger.LogInformation(
                    "Invalid RAM value: {RamTotalKB}. Skipping...",
                    context.Snapshot.Memory.TotalKB
                );
                return Task.FromResult(
                    ApplyResult.False(
                        Loc.Instance[$"{ErrorPrefix}.InvalidRAM", context.Snapshot.Memory.TotalKB]
                    )
                );
            }

            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control",
                    "SvcHostSplitThresholdInKB",
                    context.Snapshot.Memory.TotalKB,
                    RegistryValueKind.DWord
                )
            );
            context.Logger.LogInformation(
                "Consolidated service hosts with threshold: {ThresholdKB} KB",
                context.Snapshot.Memory.TotalKB
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "C51E4187-BE49-4376-A97D-46C967A033B5",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.System | OptimizationTags.Performance
    )]
    public class ProcessPriority : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            /*
             ref: https://forums.blurbusters.com/viewtopic.php?t=8535
            42 Dec, 2A Hex = Short, Fixed , High foreground boost.
            41 Dec, 29 Hex = Short, Fixed , Medium foreground boost.
            40 Dec, 28 Hex = Short, Fixed , No foreground boost.

            38 Dec, 26 Hex = Short, Variable , High foreground boost.
            37 Dec, 25 Hex = Short, Variable , Medium foreground boost.
            36 Dec, 24 Hex = Short, Variable , No foreground boost.

            26 Dec, 1A Hex = Long, Fixed, High foreground boost.
            25 Dec, 19 Hex = Long, Fixed, Medium foreground boost.
            24 Dec, 18 Hex = Long, Fixed, No foreground boost.

            22 Dec, 16 Hex = Long, Variable, High foreground boost.
            21 Dec, 15 Hex = Long, Variable, Medium foreground boost.
            20 Dec, 14 Hex = Long, Variable, No foreground boost.
             */

            const int win32Priority = 38; // Short, Variable, High foreground boost

            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl",
                    "Win32PrioritySeparation",
                    win32Priority
                )
            );
            context.Logger.LogInformation(
                "Enabled foreground boost with priority: {Priority}",
                win32Priority
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "FFB49D94-CCA9-4591-B329-6FDA3A2758F9",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.System
            | OptimizationTags.Performance
            | OptimizationTags.Latency
            | OptimizationTags.Audio
            | OptimizationTags.Display
    )]
    public class OptimizeMultimediaScheduler : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            const string systemProfileKey =
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

            RegistryService.Write(
                context,
                new RegistryItem(systemProfileKey, "NoLazyMode", 1),
                new RegistryItem(systemProfileKey, "AlwaysOn", 1),
                new RegistryItem(
                    systemProfileKey,
                    "NetworkThrottlingIndex",
                    unchecked((int)0xFFFFFFFF),
                    RegistryValueKind.DWord
                ),
                new RegistryItem(
                    systemProfileKey,
                    "SystemResponsiveness",
                    10,
                    RegistryValueKind.DWord
                ) // minimum possible value (Values below 10 and above 100 are clamped to 20.)
            );

            RegistryService.Write(
                context,
                new RegistryItem($@"{systemProfileKey}\Tasks\Games", "Priority", 2),
                new RegistryItem($@"{systemProfileKey}\Tasks\Games", "Scheduling Category", "High"),
                new RegistryItem($@"{systemProfileKey}\Tasks\Games", "SFIO Priority", "High"),
                new RegistryItem($@"{systemProfileKey}\Tasks\Games", "GPU Priority", 8)
            );

            context.Logger.LogInformation(
                "Optimized Multimedia Class Scheduler Service (MMCSS) for gaming and low latency"
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "613FE85C-770D-441C-B97A-147B89B99028",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Latency | OptimizationTags.System | OptimizationTags.Performance
    )]
    public class KeyboardLatencyOptimization : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "KeyboardDelay", "0"),
                new RegistryItem(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "KeyboardSpeed", "31")
            );

            context.Logger.LogInformation("Optimized keyboard repeat settings");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "B7BB32F8-C756-47A4-83F2-F6E7EC7D45B8",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Latency | OptimizationTags.System
    )]
    public class DisableAccessibilityKeyboardHotkeys : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKEY_CURRENT_USER\Control Panel\Accessibility\StickyKeys",
                    "Flags",
                    "26"
                ),
                new RegistryItem(
                    @"HKEY_CURRENT_USER\Control Panel\Accessibility\Keyboard Response",
                    "Flags",
                    "2"
                ),
                new RegistryItem(
                    @"HKEY_CURRENT_USER\Control Panel\Accessibility\ToggleKeys",
                    "Flags",
                    "34"
                )
            );

            context.Logger.LogInformation("Disabled accessibility keyboard hotkeys");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "D5A2F7C4-1E8B-4C93-B6D0-8F41AE92C7E3",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.System | OptimizationTags.Network | OptimizationTags.Performance
    )]
    public class LiftWebDavFileSizeLimit : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters",
                    "FileSizeLimitInBytes",
                    unchecked((int)0xFFFFFFFF),
                    RegistryValueKind.DWord
                )
            );

            context.Logger.LogInformation("Lifted the WebDAV file size limit to 4 GB");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     Zentra ACT-TCP-NORMAL (plan phase 4): sets TCP receive window auto-tuning back to
    ///     normal when something disabled or restricted it. The previous level is read in a
    ///     language-independent way and restored on revert.
    /// </summary>
    [Optimization(
        Id = "5D8A2F17-B6C3-4E91-8A04-E3F71C2B9D56",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Network | OptimizationTags.Performance
    )]
    public class TcpAutoTuningNormal : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var query = await context.Shell.QueryPowerShellAsync(
                "(Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal",
                context.Logger,
                ct: context.CancellationToken
            );
            var current = NetshLevel(query.Stdout);

            if (current is null)
            {
                context.Changes.Add(
                    ServiceStrings.ShellName,
                    "Get-NetTCPSetting",
                    false,
                    error: Loc.Instance[$"{ErrorPrefix}.ReadFailed"],
                    errorDetail: query.Stdout + query.Stderr
                );
                return context.Changes.ToApplyResult();
            }

            if (current == "normal")
            {
                context.Changes.AddSkip(Name, "autotuninglevel: normal");
                return context.Changes.ToApplyResult();
            }

            await context.Shell.CMDAsync(
                "netsh int tcp set global autotuninglevel=normal",
                context,
                RevertCommand(current)
            );
            return context.Changes.ToApplyResult();
        }

        /// <summary>Maps Get-NetTCPSetting's level name to the netsh keyword, or null if unknown.</summary>
        internal static string? NetshLevel(string? powerShellLevel) =>
            powerShellLevel?.Trim() switch
            {
                "Normal" => "normal",
                "Disabled" => "disabled",
                "Restricted" => "restricted",
                "HighlyRestricted" => "highlyrestricted",
                "Experimental" => "experimental",
                _ => null,
            };

        /// <summary>The command that puts the previous level back.</summary>
        internal static string RevertCommand(string netshLevel) =>
            $"netsh int tcp set global autotuninglevel={netshLevel}";
    }

    /// <summary>
    ///     Zentra ACT-BROWSER-BG (plan section 1.1 and phase 6B): Edge and Chrome stop running in
    ///     the background after they are closed, and Edge stops preloading at sign-in. Frees real
    ///     RAM without touching WebView2. The browsers then show "managed by your organization":
    ///     tell the client it is this setting.
    /// </summary>
    [Optimization(
        Id = "D5A8E3C1-7B42-4F96-9E0D-28C6B1F4A753",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Ram | OptimizationTags.Performance
    )]
    public class DisableBrowserBackground : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Edge",
                    "StartupBoostEnabled",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Edge",
                    "BackgroundModeEnabled",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Google\Chrome",
                    "BackgroundModeEnabled",
                    0
                )
            );
            context.Logger.LogInformation("Browsers no longer run in the background");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     Zentra ACT-NIC-POWER (plan phase 6B): connected physical network adapters stop saving
    ///     power, which can drop the connection. -NoRestart keeps the adapter up, so a RustDesk
    ///     session is not cut; the change takes effect on the next restart of the adapter or PC.
    /// </summary>
    [Optimization(
        Id = "E2B94C68-1D7F-4A53-B8E6-90C3F5A21D47",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Network
    )]
    public class DisableNicPowerSaving : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var query = await context.Shell.QueryPowerShellAsync(
                // Only adapters whose driver exposes power management; others have nothing to change.
                "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Where-Object { Get-NetAdapterPowerManagement -Name $_.Name -ErrorAction SilentlyContinue } | ForEach-Object { $_.Name }",
                context.Logger,
                ct: context.CancellationToken
            );
            var adapters = (query.Stdout ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .ToList();

            if (adapters.Count == 0)
            {
                context.Changes.AddNotApplicable(Name, Loc.Instance[$"{ErrorPrefix}.NoAdapter"]);
                return context.Changes.ToApplyResult();
            }

            foreach (var adapter in adapters)
            {
                var name = optimizerDuck.Common.Helpers.PowerShellText.EscapeSingleQuoted(adapter);
                await context.Shell.PowerShellAsync(
                    $"Disable-NetAdapterPowerManagement -Name '{name}' -NoRestart",
                    context,
                    $"Enable-NetAdapterPowerManagement -Name '{name}' -NoRestart"
                );
            }
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Zentra ACT-INDEX-EXCLUDE (plan phase 6B): Downloads and game library folders leave the
    ///     Windows Search index, so the indexer stops churning through huge folders nobody
    ///     searches by name. Done through Windows Search's own interface (as Indexing Options
    ///     does) and undone by removing each exclusion.
    /// </summary>
    [Optimization(
        Id = "F8C35A2E-9B47-4D16-A3E0-6D2B8F1C74E9",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Disk | OptimizationTags.Performance
    )]
    public class ExcludeLargeFoldersFromIndex : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            if (!Services.Diagnostics.ConsoleUser.IsCurrentProcessUser())
            {
                // The folders come from HKCU: another account's Downloads would be excluded.
                context.Changes.AddRefused(Name, Loc.Instance[$"{ErrorPrefix}.OtherUser"]);
                return Task.FromResult(context.Changes.ToApplyResult());
            }

            if (!SearchIndexService.IsAvailable())
            {
                // Windows Search is off or missing: there is no index to relieve.
                context.Changes.AddNotApplicable(
                    Name,
                    Loc.Instance[$"{ErrorPrefix}.SearchUnavailable"]
                );
                return Task.FromResult(context.Changes.ToApplyResult());
            }

            foreach (var folder in SearchIndexService.LargeFolders())
            {
                try
                {
                    if (!SearchIndexService.IsIndexed(folder))
                    {
                        context.Changes.AddSkip(Name, folder);
                        continue;
                    }
                    if (context.Preview is { } plan)
                    {
                        plan.Add(PlannedChangeKind.Command, folder, "indexed", "excluded");
                        continue;
                    }
                    var ok = SearchIndexService.Exclude(folder);
                    if (!ok)
                    {
                        // Windows did not confirm it: take the rule back out, so nothing is left
                        // behind that a revert would not know about.
                        context.Logger.LogWarning(
                            "Index exclusion of {Folder} not confirmed; rule removed",
                            folder
                        );
                        SearchIndexService.RemoveExclusion(folder);
                    }
                    context.Changes.Add(
                        Name,
                        folder,
                        ok,
                        ok ? new Revert.Steps.IndexExclusionRevertStep { Folder = folder } : null,
                        error: ok ? null : Loc.Instance[$"{ErrorPrefix}.NotExcluded"]
                    );
                }
                catch (global::System.Runtime.InteropServices.COMException ex)
                {
                    context.Changes.Add(
                        Name,
                        folder,
                        false,
                        error: Loc.Instance[$"{ErrorPrefix}.SearchUnavailable"],
                        errorDetail: ex.Message
                    );
                }
            }

            if (context.Changes.Changes.Count == 0)
                context.Changes.AddNotApplicable(Name, Loc.Instance[$"{ErrorPrefix}.NoFolders"]);
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     Zentra ACT-GAMEMODE-ADV (plan section 1.1, phase 7): turns off Memory Integrity (HVCI)
    ///     and the Virtual Machine Platform, which cost frames in some games. Risky: it lowers the
    ///     protection against kernel attacks, so it needs written consent, only for clients who
    ///     play, after an A/B test with the same game. Takes effect after a restart; the Session
    ///     page keeps a visible button to turn the protection back on (revert).
    /// </summary>
    [Optimization(
        Id = "9D4A7E16-B3C2-4F58-81E9-6A0F2C5D8B37",
        Risk = OptimizationRisk.Risky,
        Tags = OptimizationTags.Performance | OptimizationTags.Security
    )]
    public class AdvancedGameMode : BaseOptimization
    {
        public const string HvciKey =
            @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

        private static bool IsLockedOrManaged()
        {
            using var scenario = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"
            );
            if (scenario?.GetValue("Locked") is int locked && locked != 0)
                return true;
            using var policy = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard"
            );
            return policy?.GetValue("HypervisorEnforcedCodeIntegrity") is not null;
        }

        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            // With a UEFI lock or a Device Guard policy the registry value changes nothing, and
            // the Session card would wrongly say the protection is off.
            if (IsLockedOrManaged())
            {
                context.Changes.AddNotApplicable(Name, Loc.Instance[$"{ErrorPrefix}.Locked"]);
                return context.Changes.ToApplyResult();
            }

            // A preview shows the changes; the client is asked only when they are applied.
            if (
                context.Preview is null
                && !await Services.System.ClientConsent.RequestAsync(
                    Name,
                    Loc.Instance[$"{ErrorPrefix}.Consent"],
                    context.Logger
                )
            )
            {
                context.Changes.AddRefused(Name, Loc.Instance["Consent.NotGiven"]);
                return context.Changes.ToApplyResult();
            }

            RegistryService.Write(context, new RegistryItem(HvciKey, "Enabled", 0));

            var vmp = await context.Shell.QueryPowerShellAsync(
                "(Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State",
                context.Logger,
                ct: context.CancellationToken
            );
            if (vmp.Stdout?.Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase) == true)
                await context.Shell.PowerShellAsync(
                    "Disable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart | Out-Null",
                    context,
                    "Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart | Out-Null"
                );
            else
                context.Changes.AddSkip(Name, $"VirtualMachinePlatform: {vmp.Stdout?.Trim()}");

            if (Application.Current is App app)
                app.HasPendingChanges = true; // a restart applies it
            context.Logger.LogInformation("Advanced game mode applied; restart required");
            return context.Changes.ToApplyResult();
        }
    }
}
