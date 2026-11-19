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

[OptimizationCategory(typeof(AIOptimizerPage))]
public class AI : LocalizedObject, IOptimizationCategory
{
    public string Name => Loc.Instance[$"Optimizer.{nameof(AI)}"];
    public OptimizationCategoryOrder Order { get; init; } = OptimizationCategoryOrder.AI;
    public ObservableCollection<IOptimization> Optimizations { get; init; } = [];

    [Optimization(
        Id = "8462E718-42CA-4BE6-940C-3620BF52A533",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Privacy | OptimizationTags.System | OptimizationTags.Windows11Only,
        Condition = typeof(RecallInstalledCondition)
    )]
    public class DisableRecall : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            // DisableAIDataAnalysis stops new snapshots (and deletes existing ones);
            // TurnOffSavingSnapshots mirrors the documented "Turn off saving snapshots"
            // policy for broad build coverage; AllowRecallEnablement is device-scope only
            // and removes the Recall bits on Windows 11 24H2+ (Build 26100.3915+).
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                    "DisableAIDataAnalysis",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                    "AllowRecallEnablement",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                    "TurnOffSavingSnapshots",
                    1
                ),
                new RegistryItem(
                    @"HKCU\Software\Policies\Microsoft\Windows\WindowsAI",
                    "DisableAIDataAnalysis",
                    1
                ),
                new RegistryItem(
                    @"HKCU\Software\Policies\Microsoft\Windows\WindowsAI",
                    "TurnOffSavingSnapshots",
                    1
                )
            );
            context.Logger.LogInformation("Disabled Windows Recall AI snapshots");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "962C9960-6BFA-4FC5-97D9-BFE772426E39",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.System | OptimizationTags.Windows11Only,
        Condition = typeof(Windows11_24H2OrGreaterCondition)
    )]
    public class DisableClickToDo : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                    "DisableClickToDo",
                    1
                ),
                new RegistryItem(
                    @"HKCU\Software\Policies\Microsoft\Windows\WindowsAI",
                    "DisableClickToDo",
                    1
                )
            );
            context.Logger.LogInformation("Disabled Click To Do AI overlay");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    // -----------------------------------------------------------------------------------------
    // From Win11Debloat (https://github.com/Raphire/Win11Debloat), MIT License,
    // Copyright (c) 2020 Raphire. Values taken from its Regfiles as of 2026-11-15; see
    // THIRD-PARTY-NOTICES.md. Each one is reverted from the values recorded on apply.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    ///     Win11Debloat "Disable_AI_Service_Auto_Start": the Windows AI service (WSAIFabricSvc)
    ///     goes to Manual, never Disabled, so anything that needs it can still start it.
    /// </summary>
    [Optimization(
        Id = "0B6E2F4C-7A51-4C8E-9D3B-5E1F8A2C6D40",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Performance
            | OptimizationTags.System
            | OptimizationTags.Windows11Only
    )]
    public class SetAiServiceToManual : BaseOptimization
    {
        internal static readonly ServiceItem Service = new(
            "WSAIFabricSvc",
            ServiceStartupType.Manual
        );

        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            await ServiceProcessService.ChangeServiceStartupTypeAsync(context, Service);
            context.Logger.LogInformation("Windows AI service set to Manual");
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>Win11Debloat "Disable_Edge_AI_Features": Copilot and generative AI in Edge.</summary>
    [Optimization(
        Id = "6C2D9A17-3E84-4B5F-A0C6-81D7E4F92B35",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.System
    )]
    public class DisableEdgeAiFeatures : BaseOptimization
    {
        private const string Key = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";

        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(Key, "CopilotCDPPageContext", 0),
                new RegistryItem(Key, "CopilotPageContext", 0),
                new RegistryItem(Key, "HubsSidebarEnabled", 0),
                new RegistryItem(Key, "Microsoft365CopilotChatIconEnabled", 0),
                new RegistryItem(Key, "EdgeEntraCopilotPageContext", 0),
                new RegistryItem(Key, "EdgeHistoryAISearchEnabled", 0),
                new RegistryItem(Key, "ComposeInlineEnabled", 0),
                new RegistryItem(Key, "GenAILocalFoundationalModelSettings", 1),
                new RegistryItem(Key, "NewTabPageBingChatEnabled", 0)
            );
            context.Logger.LogInformation("Disabled AI features in Edge");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>Win11Debloat "Disable_Paint_AI_Features": Cocreator, generative fill and erase.</summary>
    [Optimization(
        Id = "A94F1C3B-2D67-4E80-B5A9-3C7E0D18F6B2",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.Windows11Only
    )]
    public class DisablePaintAiFeatures : BaseOptimization
    {
        private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint";

        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(Key, "DisableCocreator", 1),
                new RegistryItem(Key, "DisableGenerativeFill", 1),
                new RegistryItem(Key, "DisableImageCreator", 1),
                new RegistryItem(Key, "DisableGenerativeErase", 1),
                new RegistryItem(Key, "DisableRemoveBackground", 1)
            );
            context.Logger.LogInformation("Disabled AI features in Paint");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>Win11Debloat "Disable_Notepad_AI_Features": rewrite and summarize in Notepad.</summary>
    [Optimization(
        Id = "E1B7C52D-9F03-4A6B-8C14-D2A6F0E37B91",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.Windows11Only
    )]
    public class DisableNotepadAiFeatures : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(@"HKLM\SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)
            );
            context.Logger.LogInformation("Disabled AI features in Notepad");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }
}
