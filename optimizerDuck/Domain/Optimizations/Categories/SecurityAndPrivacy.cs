using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.Logging;
using optimizerDuck.Common.Extensions;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Conditions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.ScheduledTask;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.UI.Pages.Optimize.Categories;

namespace optimizerDuck.Domain.Optimizations.Categories;

[OptimizationCategory(typeof(SecurityAndPrivacyOptimizerPage))]
public class SecurityAndPrivacy : LocalizedObject, IOptimizationCategory
{
    public string Name => Loc.Instance[$"Optimizer.{nameof(SecurityAndPrivacy)}"];
    public OptimizationCategoryOrder Order { get; init; } =
        OptimizationCategoryOrder.SecurityAndPrivacy;
    public ObservableCollection<IOptimization> Optimizations { get; init; } = [];

    [Optimization(
        Id = "74DC8DAC-1F90-4BBD-ACF7-7E626749D71C",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Security | OptimizationTags.Privacy | OptimizationTags.System
    )]
    public class DisableTelemetry : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            progress?.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance[$"{ProgressPrefix}.EditRegistry"],
                    IsIndeterminate = false,
                    Value = 0,
                    Total = 3,
                }
            );
            // @formatter:off
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection",
                    "AllowTelemetry",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "AllowTelemetry",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "DoNotShowFeedbackNotifications",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "AllowCommercialDataPipeline",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "AllowDeviceNameInTelemetry",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "MicrosoftEdgeDataOptIn",
                    0
                ),
                new RegistryItem(@"HKCU\SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                new RegistryItem(
                    @"HKCU\Software\Policies\Microsoft\Windows\EdgeUI",
                    "DisableMFUTracking",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                    "DisableInventory",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                    "AITEnable",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Policies\Microsoft\Assistance\Client\1.0",
                    "NoExplicitFeedback",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Assistance\Client\1.0",
                    "NoActiveHelp",
                    1
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy",
                    "HasAccepted",
                    0
                ),
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    "Start_TrackProgs",
                    0
                )
            );
            RegistryService.DeleteValue(
                context,
                [new RegistryItem(@"HKCU\SOFTWARE\Microsoft\Siuf\Rules", "PeriodInNanoSeconds")]
            );
            context.Logger.LogInformation("Reduced core telemetry and feedback");
            progress?.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance[$"{ProgressPrefix}.DisableServices"],
                    IsIndeterminate = false,
                    Value = 1,
                    Total = 3,
                }
            );
            await ServiceProcessService.ChangeServiceStartupTypeAsync(
                context,
                [
                    new ServiceItem("DiagTrack", ServiceStartupType.Disabled),
                    new ServiceItem("dmwappushservice", ServiceStartupType.Disabled),
                    new ServiceItem("DcpSvc", ServiceStartupType.Disabled),
                    new ServiceItem(
                        "diagnosticshub.standardcollector.service",
                        ServiceStartupType.Disabled
                    ),
                ]
            );

            progress?.Report(
                new ProcessingProgress
                {
                    Message = Loc.Instance[$"{ProgressPrefix}.DisableTasks"],
                    IsIndeterminate = false,
                    Value = 2,
                    Total = 3,
                }
            );
            var tasksToDelete = new HashSet<string>([
                @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
                @"\Microsoft\Windows\Application Experience\MareBackup",
                @"\Microsoft\Windows\Application Experience\StartupAppTask",
                @"\Microsoft\Windows\Application Experience\PcaPatchDbTask",
                @"\Microsoft\Windows\Autochk\Proxy",
                @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                @"\Microsoft\Windows\Feedback\Siuf\DmClient",
                @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
            ]);
            // @formatter:on

            foreach (var task in tasksToDelete)
            {
                // The provider records what it found, so a task that is already disabled or
                // absent still leaves a step: deciding here would drop it from the record.
                ScheduledTaskService.DisableTask(context, task);
            }

            return context.Changes.ToApplyResult();
        }
    }

    [Optimization(
        Id = "097B1C61-F372-4B9E-A1C9-D5E6F7A8B9C0",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.System | OptimizationTags.Privacy
    )]
    public class DisableErrorReporting : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting",
                    "Disabled",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports",
                    "PreventHandwritingErrorReports",
                    1
                )
            );

            await ServiceProcessService.ChangeServiceStartupTypeAsync(
                context,
                [
                    new ServiceItem("WerSvc", ServiceStartupType.Disabled),
                    new ServiceItem("PcaSvc", ServiceStartupType.Disabled),
                ]
            );

            var tasksToDelete = new[]
            {
                @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
                @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
                @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticResolver",
                @"\Microsoft\Windows\Diagnosis\Scheduled",
            };

            foreach (var task in tasksToDelete)
            {
                // The provider records what it found, so a task that is already disabled or
                // absent still leaves a step: deciding here would drop it from the record.
                ScheduledTaskService.DisableTask(context, task);
            }

            context.Logger.LogInformation(
                "Disabled Windows Error Reporting and Compatibility Assistant"
            );
            return context.Changes.ToApplyResult();
        }
    }

    [Optimization(
        Id = "B1C2D3E4-F5A6-4B7C-8D9E-0F1A2B3C4D5E",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.System
    )]
    public class DisableAdvertisingAndSuggestions : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            // @formatter:off
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                    "Enabled",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo",
                    "DisabledByGroupPolicy",
                    1
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
                    "TailoredExperiencesWithDiagnosticDataEnabled",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                    "DisableTailoredExperiencesWithDiagnosticData",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                    "DisableWindowsConsumerFeatures",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                    "DisableSoftLanding",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                    "DisableThirdPartySuggestions",
                    1
                ),
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement",
                    "ScoobeSystemSettingEnabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\InputPersonalization",
                    "RestrictImplicitInkCollection",
                    1
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\InputPersonalization",
                    "RestrictImplicitTextCollection",
                    1
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\InputPersonalization\TrainedDataStore",
                    "HarvestContacts",
                    0
                ),
                new RegistryItem(
                    @"HKCU\Control Panel\International\User Profile",
                    "HttpAcceptLanguageOptOut",
                    1
                )
            );
            // @formatter:on
            context.Logger.LogInformation(
                "Disabled advertising ID, consumer features and system suggestions"
            );

            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     Zentra: the official "Allow widgets = Disabled" policy, set through the local Group
    ///     Policy like gpedit.msc does. Writing the registry directly (what optimizerDuck did) is
    ///     blocked by the User Choice Protection Driver on current Windows 11, even for an
    ///     administrator, and so was the old HKCU Feeds value (a Windows 10 taskbar setting).
    /// </summary>
    [Optimization(
        Id = "26F3AE39-4EAA-45D4-9375-B244159D5EB3",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.System | OptimizationTags.Windows11Only,
        Condition = typeof(WidgetsPolicyCondition)
    )]
    public class DisableNewsAndInterests : BaseOptimization
    {
        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            await LocalGroupPolicyService.SetMachineDwordAsync(
                context,
                @"Software\Policies\Microsoft\Dsh",
                "AllowNewsAndInterests",
                0
            );

            context.Logger.LogInformation("Widgets policy set to not allowed");
            return context.Changes.ToApplyResult();
        }
    }

    [Optimization(
        Id = "6D217B90-0957-4861-B3F5-58ECF3E236EE",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.Windows10Only | OptimizationTags.System,
        Condition = typeof(Windows10Condition)
    )]
    public class HideMeetNowButton : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                    "HideSCAMeetNow",
                    1
                )
            );

            context.Logger.LogInformation("Hidden Meet Now button from the taskbar");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.System
    )]
    public class DisableActivityHistory : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                    "PublishUserActivities",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                    "EnableActivityFeed",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                    "PublishUserActivitiesOnUserConsent",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                    "UploadUserActivities",
                    0
                )
            );

            context.Logger.LogInformation("Disabled activity history collection and syncing");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "C1D2E3F4-A5B6-4C7D-8E9F-0A1B2C3D4E5F",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.System
    )]
    public class DisableLocationAndSensors : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                    "DisableLocation",
                    1
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                    "DisableSensors",
                    1
                ),
                new RegistryItem(@"HKLM\SYSTEM\Maps", "AutoUpdateEnabled", 0),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Permissions\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}",
                    "SensorPermissionState",
                    0
                ),
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location",
                    "Value",
                    "Deny"
                ),
                new RegistryItem(
                    @"HKLM\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location",
                    "Value",
                    "Deny"
                ),
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Geolocation",
                    "Status",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Services\lfsvc\Service\Configuration",
                    "Status",
                    0
                ),
                new RegistryItem(
                    @"HKLM\Software\Microsoft\PolicyManager\default\WiFi\AllowWiFiHotSpotReporting",
                    "Value",
                    0
                ),
                new RegistryItem(
                    @"HKLM\Software\Microsoft\PolicyManager\default\WiFi\AllowAutoConnectToWiFiSenseHotspots",
                    "Value",
                    0
                )
            );

            const string mapsTask = @"\Microsoft\Windows\Maps\MapsUpdateTask";
            var mapsState = ScheduledTaskService.GetTaskEnabledState(mapsTask, context.Logger);
            if (mapsState is not (TaskEnabledState.Disabled or TaskEnabledState.NotFound))
                ScheduledTaskService.DisableTask(context, mapsTask);

            context.Logger.LogInformation(
                "Disabled location tracking, sensors and offline maps updates"
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "4107430C-0074-4380-90E7-3662572E4720",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Privacy | OptimizationTags.System | OptimizationTags.Performance
    )]
    public class DisableAutoLogger : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\AppModel",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\Cellcore",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\CloudExperienceHostOobe",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\DataMarket",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\DiagLog",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\Diagtrack-Listener",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\LwtNetLog",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\SQMLogger",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\WdiContextLog",
                    "Start",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\WiFiSession",
                    "Start",
                    0
                )
            );
            context.Logger.LogInformation("Disabled WMI AutoLogger sessions");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "6856782A-B530-4623-BD89-942D73FB82FD",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Privacy | OptimizationTags.System | OptimizationTags.Windows10Only,
        Condition = typeof(Windows10Condition)
    )]
    public class DisableCortana : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                    "AllowCortana",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                    "AllowCloudSearch",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                    "AllowCortanaAboveLock",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                    "AllowSearchToUseLocation",
                    0
                ),
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                    "ConnectedSearchUseWeb",
                    0
                ),
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Search",
                    "CortanaConsent",
                    0
                ),
                new RegistryItem(
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Search",
                    "CortanaConsent2",
                    0
                )
            );
            context.Logger.LogInformation("Disabled Cortana and web search");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "64C6BEC3-B58C-4E57-830A-1DE1F4650542",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Privacy
            | OptimizationTags.System
            | OptimizationTags.Windows11Only
            | OptimizationTags.Visual,
        Condition = typeof(Windows11Condition)
    )]
    public class DisableCopilot : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot",
                    "TurnOffWindowsCopilot",
                    1
                ),
                new RegistryItem(
                    @"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot",
                    "TurnOffWindowsCopilot",
                    1
                ),
                new RegistryItem(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    "ShowCopilotButton",
                    0
                ),
                new RegistryItem(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
                    "{CB3B0003-8088-4EDE-8769-8B354AB2FF8C}",
                    ""
                )
            );

            context.Logger.LogInformation("Disabled Windows Copilot");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "00C997FE-1CB7-41BD-B473-65A81333AEE9",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.System | OptimizationTags.Performance | OptimizationTags.Privacy
    )]
    public class DisableContentDeliveryManager : BaseOptimization
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
                    "ContentDeliveryAllowed",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "SubscribedContent-338387Enabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "SubscribedContent-338388Enabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "SubscribedContent-338389Enabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "SubscribedContent-353698Enabled",
                    0
                ),
                new RegistryItem(
                    @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                    "SystemPaneSuggestionsEnabled",
                    0
                )
            );
            context.Logger.LogInformation("Disabled content delivery manager");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "A4823C34-0BDD-4418-83A0-4968102D1771",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy | OptimizationTags.System
    )]
    public class DisableFindMyDevice : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\FindMyDevice",
                    "AllowFindMyDevice",
                    0
                )
            );
            context.Logger.LogInformation("Disabled Find My Device location tracking");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    [Optimization(
        Id = "6F242AD6-4717-4161-A7BF-A2AF5F0C9BDD",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Network | OptimizationTags.Privacy
    )]
    public class DisableDeliveryOptimization : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
                    "DODownloadMode",
                    0
                )
            );
            context.Logger.LogInformation("Disabled peer-to-peer Delivery Optimization sharing");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     Zentra ACT-PUA-ON (plan phase 6B): Defender blocks potentially unwanted apps. Revert
    ///     goes back to audit mode, never to off, even if it was off before (Diego's decision,
    ///     19/11/2026: the blacklist never turns Defender protections off).
    /// </summary>
    [Optimization(
        Id = "8E4B1C7A-2F69-4D3E-A1B5-C9D07E6F3A28",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Security
    )]
    public class EnablePuaProtection : BaseOptimization
    {
        internal const string RevertCommand = "Set-MpPreference -PUAProtection AuditMode";

        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var query = await context.Shell.QueryPowerShellAsync(
                "(Get-MpPreference).PUAProtection",
                context.Logger,
                ct: context.CancellationToken
            );

            if (!int.TryParse(query.Stdout?.Trim(), out var current))
            {
                context.Changes.Add(
                    ServiceStrings.ShellName,
                    "Get-MpPreference",
                    false,
                    error: Loc.Instance[$"{ErrorPrefix}.ReadFailed"],
                    errorDetail: query.Stdout + query.Stderr
                );
                return context.Changes.ToApplyResult();
            }

            if (current == 1)
            {
                context.Changes.AddSkip(Name, "PUAProtection: Enabled");
                return context.Changes.ToApplyResult();
            }

            await context.Shell.PowerShellAsync(
                "Set-MpPreference -PUAProtection Enabled",
                context,
                RevertCommand
            );
            context.Logger.LogInformation("PUA protection enabled (was {Previous})", current);
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Zentra ACT-WU-PAUSE7 (plan section 1.1, phase 7): the native Windows Update pause,
    ///     the same values Settings writes, for 7 days at most. It ends by itself; reverting
    ///     resumes updates now. Only when the client asks (a tournament, a deadline), with written
    ///     consent. The change records the end date.
    /// </summary>
    [Optimization(
        Id = "7B3E9D25-A1C8-4F64-B2D0-5E8C3A71F946",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.System
    )]
    public class PauseWindowsUpdate7Days : BaseOptimization
    {
        public const int MaxDays = 7;
        private const string Key = @"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            var now = DateTime.UtcNow;
            if (BlockedUntil(now, ReadExpiry(), ReadLastZentraPause()) is { } blocked)
            {
                context.Changes.AddRefused(
                    Name,
                    Loc.Instance[
                        $"{ErrorPrefix}.RecentPause",
                        blocked.ToLocalTime().ToString("f", Loc.CurrentCulture)
                    ]
                );
                return context.Changes.ToApplyResult();
            }

            var until = now.AddDays(MaxDays);
            // A preview shows the changes; the client is asked only when they are applied.
            if (
                context.Preview is null
                && !await ClientConsent.RequestAsync(
                    Name,
                    Loc.Instance[
                        $"{ErrorPrefix}.Consent",
                        until.ToLocalTime().ToString("f", Loc.CurrentCulture)
                    ],
                    context.Logger
                )
            )
            {
                context.Changes.AddRefused(Name, Loc.Instance["Consent.NotGiven"]);
                return context.Changes.ToApplyResult();
            }

            var items = PauseValues(DateTime.UtcNow, until)
                .Select(v => new RegistryItem(Key, v.Name, v.Value))
                .ToArray();
            RegistryService.Write(context, items);
            if (context.Preview is null && context.Changes.HasSuccessfulSteps)
                RememberZentraPause(until);
            context.Logger.LogInformation("Windows Update paused until {Until:u}", until);
            return context.Changes.ToApplyResult();
        }

        /// <summary>
        ///     Days that must pass after a pause ends before Zentra pauses again, so pauses cannot
        ///     be chained (apply, revert, apply) into weeks without security patches.
        /// </summary>
        public const int CooldownDays = 7;

        private const string Format = "yyyy-MM-ddTHH:mm:ssZ";

        private static string LastPauseFile =>
            Path.Combine(Shared.RootDirectory, "wu-pause-last.txt");

        /// <summary>
        ///     Until when a new pause is refused: a pause still running, or one that ended less
        ///     than <see cref="CooldownDays" /> ago, whether Windows (the registry) or Zentra (its
        ///     own note, which survives a revert) knows about it. Null when a pause is allowed.
        /// </summary>
        internal static DateTime? BlockedUntil(
            DateTime nowUtc,
            DateTime? registryExpiryUtc,
            DateTime? lastZentraEndUtc
        )
        {
            var latest = new[] { registryExpiryUtc, lastZentraEndUtc }.Max();
            if (latest is null)
                return null;
            var free = latest.Value.AddDays(CooldownDays);
            return free > nowUtc ? free : null;
        }

        internal static DateTime? ParseUtc(string? value) =>
            DateTime.TryParseExact(
                value?.Trim(),
                Format,
                global::System.Globalization.CultureInfo.InvariantCulture,
                global::System.Globalization.DateTimeStyles.AdjustToUniversal
                    | global::System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed
            )
                ? parsed
                : null;

        private static DateTime? ReadExpiry()
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"
            );
            return ParseUtc(key?.GetValue("PauseUpdatesExpiryTime") as string);
        }

        private static DateTime? ReadLastZentraPause()
        {
            try
            {
                return File.Exists(LastPauseFile)
                    ? ParseUtc(File.ReadAllText(LastPauseFile))
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void RememberZentraPause(DateTime untilUtc)
        {
            try
            {
                Directory.CreateDirectory(Shared.RootDirectory);
                File.WriteAllText(
                    LastPauseFile,
                    untilUtc.ToString(
                        Format,
                        global::System.Globalization.CultureInfo.InvariantCulture
                    )
                );
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>The native pause values (ISO 8601, UTC), as Settings writes them.</summary>
        internal static IReadOnlyList<(string Name, string Value)> PauseValues(
            DateTime startUtc,
            DateTime endUtc
        )
        {
            var start = startUtc.ToString(
                "yyyy-MM-ddTHH:mm:ssZ",
                global::System.Globalization.CultureInfo.InvariantCulture
            );
            var end = endUtc.ToString(
                "yyyy-MM-ddTHH:mm:ssZ",
                global::System.Globalization.CultureInfo.InvariantCulture
            );
            return
            [
                ("PauseUpdatesStartTime", start),
                ("PauseUpdatesExpiryTime", end),
                ("PauseFeatureUpdatesStartTime", start),
                ("PauseFeatureUpdatesEndTime", end),
                ("PauseQualityUpdatesStartTime", start),
                ("PauseQualityUpdatesEndTime", end),
            ];
        }
    }

    /// <summary>
    ///     Zentra ACT-DEFENDER-EXCL (plan section 1.1, phase 7): Defender stops scanning the game
    ///     library folders only (Steam, Epic); it keeps protecting everything else. Never a drive,
    ///     Windows, Downloads, Temp, the Desktop or user folders. With written consent. Reverting
    ///     removes each exclusion.
    /// </summary>
    [Optimization(
        Id = "C5F1A8D3-6E29-4B07-9A4C-2D8B7E3F1C65",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Security | OptimizationTags.Performance
    )]
    public class DefenderGameLibraryExclusion : BaseOptimization
    {
        /// <summary>The folders to exclude; replaced in tests.</summary>
        internal static Func<IReadOnlyList<string>> FolderSource { get; set; } =
            () => GameLibraries.Folders().Where(GameLibraries.IsSafeToExclude).ToList();

        public override async Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            if (!Services.Diagnostics.ConsoleUser.IsCurrentProcessUser())
            {
                context.Changes.AddRefused(Name, Loc.Instance[$"{ErrorPrefix}.OtherUser"]);
                return context.Changes.ToApplyResult();
            }

            var folders = FolderSource();
            if (folders.Count == 0)
            {
                context.Changes.AddNotApplicable(Name, Loc.Instance[$"{ErrorPrefix}.NoFolders"]);
                return context.Changes.ToApplyResult();
            }

            // A preview shows the changes; the client is asked only when they are applied.
            if (
                context.Preview is null
                && !await ClientConsent.RequestAsync(
                    Name,
                    Loc.Instance[
                        $"{ErrorPrefix}.Consent",
                        string.Join(Environment.NewLine, folders)
                    ],
                    context.Logger
                )
            )
            {
                context.Changes.AddRefused(Name, Loc.Instance["Consent.NotGiven"]);
                return context.Changes.ToApplyResult();
            }

            var current = await context.Shell.QueryPowerShellAsync(
                "(Get-MpPreference).ExclusionPath",
                context.Logger,
                ct: context.CancellationToken
            );
            if (current.ExitCode != 0)
            {
                // Without the current list an exclusion the client already had would be added
                // again, and its revert would remove it.
                context.Changes.AddRefused(Name, Loc.Instance[$"{ErrorPrefix}.ReadFailed"]);
                return context.Changes.ToApplyResult();
            }
            var existing = (current.Stdout ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimEnd('\\'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                if (existing.Contains(folder))
                {
                    context.Changes.AddSkip(Name, folder);
                    continue;
                }
                var quoted = PowerShellText.Quote(folder);
                await context.Shell.PowerShellAsync(
                    $"Add-MpPreference -ExclusionPath {quoted}",
                    context,
                    $"Remove-MpPreference -ExclusionPath {quoted}"
                );
            }
            return context.Changes.ToApplyResult();
        }
    }

    /// <summary>
    ///     Edge without telemetry, shopping, rewards, recommendations or the first-run screens,
    ///     through its policies (idea from WinUtil, MIT). Edge keeps working; it shows "managed by
    ///     your organization", as with the background setting. Collections are left alone.
    /// </summary>
    [Optimization(
        Id = "BC1A637C-BBF0-492B-8E4A-2CE27A1BB933",
        Risk = OptimizationRisk.Safe,
        Tags = OptimizationTags.Privacy
    )]
    public class DisableEdgeTelemetryAndAds : BaseOptimization
    {
        private const string Edge = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";

        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SOFTWARE\Policies\Microsoft\EdgeUpdate",
                    "CreateDesktopShortcutDefault",
                    0
                ),
                new RegistryItem(Edge, "DiagnosticData", 0),
                new RegistryItem(Edge, "PersonalizationReportingEnabled", 0),
                new RegistryItem(Edge, "UserFeedbackAllowed", 0),
                new RegistryItem(Edge, "ShowRecommendationsEnabled", 0),
                new RegistryItem(Edge, "HideFirstRunExperience", 1),
                new RegistryItem(Edge, "EdgeShoppingAssistantEnabled", 0),
                new RegistryItem(Edge, "ShowMicrosoftRewards", 0),
                new RegistryItem(Edge, "MicrosoftEdgeInsiderPromotionEnabled", 0),
                new RegistryItem(Edge, "WebWidgetAllowed", 0),
                new RegistryItem(Edge, "AlternateErrorPagesEnabled", 0),
                new RegistryItem(Edge, "ConfigureDoNotTrack", 1)
            );
            context.Logger.LogInformation("Edge telemetry and promotions turned off by policy");
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }

    /// <summary>
    ///     Stops the firmware from running programs at every boot (Windows Platform Binary Table):
    ///     some makers use it to install their apps without asking. Some laptops use it for their
    ///     anti-theft service, which stops working (idea from WinUtil, MIT).
    /// </summary>
    [Optimization(
        Id = "16220A2E-B535-473B-9B7A-1491B8F509CE",
        Risk = OptimizationRisk.Moderate,
        Tags = OptimizationTags.Security | OptimizationTags.System
    )]
    public class DisableWpbt : BaseOptimization
    {
        public override Task<ApplyResult> ApplyAsync(
            IProgress<ProcessingProgress> progress,
            OptimizationContext context
        )
        {
            RegistryService.Write(
                context,
                new RegistryItem(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager",
                    "DisableWpbtExecution",
                    1
                )
            );
            return Task.FromResult(context.Changes.ToApplyResult());
        }
    }
}
