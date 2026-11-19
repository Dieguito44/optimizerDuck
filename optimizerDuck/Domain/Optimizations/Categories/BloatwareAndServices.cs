using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
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

[OptimizationCategory(typeof(BloatwareAndServicesOptimizerPage))]
public class BloatwareAndServices : LocalizedObject, IOptimizationCategory
{
    public string Name => Loc.Instance[$"Optimizer.{nameof(BloatwareAndServices)}"];
    public OptimizationCategoryOrder Order { get; init; } =
        OptimizationCategoryOrder.BloatwareAndServices;
    public ObservableCollection<IOptimization> Optimizations { get; init; } = [];

    [Optimization(
        Id = "E3C970B0-129D-4354-BAE4-2732E5BACCF7",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.System | OptimizationTags.Disk
    )]
    public class DisablePreinstalledApps : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "PreInstalledAppsEnabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "PreInstalledAppsEverEnabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "OemPreInstalledAppsEnabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "SilentInstalledAppsEnabled",
                    0
                )
            );
            context.Logger.LogInformation("Blocked preinstalled apps");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     Zentra replacement for the upstream "200+ services" tweak (plan section 1.1,
    ///     ACT-SERVICES-MANUAL). Only a short allow-list of rarely used services moves to
    ///     Manual, never Disabled: Windows still starts them on demand. A service missing on this
    ///     machine is recorded as not applicable and skipped.
    /// </summary>
    [Optimization(
        Id = "63645B85-65A4-4500-8256-45E6C887DD0E",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.System | OptimizationTags.Performance
    )]
    public class SetRarelyUsedServicesToManual : BaseOptimization
    {
        /// <summary>The allow-list. Every entry is set to Manual, nothing else.</summary>
        internal static readonly IReadOnlyList<ServiceItem> Services =
        [
            new("Fax", ServiceStartupType.Manual),
            new("MapsBroker", ServiceStartupType.Manual),
            new("RetailDemo", ServiceStartupType.Manual),
            new("wisvc", ServiceStartupType.Manual),
            new("WMPNetworkSvc", ServiceStartupType.Manual),
        ];

        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            for (var i = 0; i < Services.Count; i++)
            {
                var service = Services[i];
                progress?.Report(
                    new ProcessingProgress
                    {
                        Message = Loc.Instance[
                            $"{ProgressPrefix}.ChangeServiceStartupType",
                            service.Name,
                            service.StartupType
                        ],
                        Value = i + 1,
                        Total = Services.Count,
                    }
                );
                await ServiceProcessService.ChangeServiceStartupTypeAsync(context, service);
            }

            context.Logger.LogInformation(
                "Rarely used services set to Manual: {Total} checked",
                Services.Count
            );
            return context.Changes.ToApplyResult();
        }
    }
}
