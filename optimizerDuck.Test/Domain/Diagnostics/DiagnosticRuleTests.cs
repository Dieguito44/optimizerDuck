using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.System;
using optimizerDuck.Test.TestDoubles;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Domain.Diagnostics;

/// <summary>Each rule against simulated snapshots: finding, pass and "not measurable".</summary>
public class DiagnosticRuleTests
{
    private static RuleResult Eval(IDiagnosticRule rule, DiagnosticSnapshot snapshot) =>
        rule.Evaluate(snapshot, Refs);

    private static DiagnosticSnapshot WithModules(params RamModuleSnapshot[] modules)
    {
        var pc = DiegoPc();
        return pc with { Ram = pc.Ram with { Modules = modules } };
    }

    // ---- R-RAM-XMP --------------------------------------------------------------------------

    [Fact]
    public void RamXmp_KitAtRatedSpeed_Passes()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new RamXmpRule(), DiegoPc()).Outcome);
    }

    [Fact]
    public void RamXmp_KitRunningAt2400_IsFound()
    {
        var snapshot = WithModules(
            Module("A", configured: 2400, reported: 2400),
            Module("B", configured: 2400, reported: 2400)
        );

        var result = Eval(new RamXmpRule(), snapshot);

        Assert.Equal(RuleOutcome.Finding, result.Outcome);
        Assert.Equal(Severity.High, result.Finding!.Severity);
        Assert.Equal([ActionIds.BiosXmp], result.Finding.ActionIds);
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "ram.rated_mts" && e.Number == 3600 && e.Text == "table"
        );
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "ram.configured_mts" && e.Number == 2400
        );
    }

    [Fact]
    public void RamXmp_UnknownPartAndSmbiosEqual_IsNotMeasurable()
    {
        var snapshot = WithModules(
            Module("A", configured: 2666, reported: 2666, part: "UNKNOWN-PART-123")
        );

        var result = Eval(new RamXmpRule(), snapshot);

        Assert.Equal(RuleOutcome.NotMeasurable, result.Outcome);
        Assert.Equal(NotMeasuredReason.NotInReferenceTable, result.Reason);
    }

    [Fact]
    public void RamXmp_UnknownPartButSmbiosAboveConfigured_UsesSmbios()
    {
        var snapshot = WithModules(Module("A", configured: 2133, reported: 3200, part: "X"));

        var result = Eval(new RamXmpRule(), snapshot);

        Assert.Equal(RuleOutcome.Finding, result.Outcome);
        Assert.Contains(result.Finding!.Evidence, e => e.Text == "smbios" && e.Number == 3200);
        Assert.Equal(Severity.Medium, result.Finding.Severity); // SMBIOS alone is a hint, not proof
    }

    [Fact]
    public void RamXmp_OnALaptopTheSmbiosSpeedIsNotEvaluated()
    {
        var snapshot = WithModules(Module("A", configured: 2666, reported: 3200, part: "X"));
        snapshot = snapshot with
        {
            Os = snapshot.Os with { DeviceKind = Measured<DeviceKind>.Of(DeviceKind.Laptop) },
        };

        Assert.Equal(RuleOutcome.NotApplicable, Eval(new RamXmpRule(), snapshot).Outcome);
    }

    [Theory]
    [InlineData("KF436C17BB/8", 3600)]
    [InlineData("F4-3200C16-8GVKB", 3200)]
    [InlineData("CMK16GX4M2B3200C16", 3200)]
    [InlineData("BL8G32C16U4B.M8FE1", 3200)]
    [InlineData("KVR26N19S8/8", 2666)]
    [InlineData("AX4U32008G16A-SB10", 3200)]
    [InlineData("VAM4UX36C188G-DVALWN", 3600)]
    public void RamKitTable_ReadsTheRatedSpeed(string part, int rated)
    {
        Assert.Equal(rated, Refs.LookupRamKit(part)?.RatedMTps);
    }

    [Fact]
    public void RamKitTable_UnknownPart_IsNull()
    {
        Assert.Null(Refs.LookupRamKit("M378A1K43CB2-CTD"));
        Assert.Null(Refs.LookupRamKit(null));
    }

    // ---- R-RAM-SINGLE / R-RAM-LOW -----------------------------------------------------------

    [Fact]
    public void RamSingle_TwoModules_Passes()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new RamSingleRule(), DiegoPc()).Outcome);
    }

    [Fact]
    public void RamSingle_OneModuleOnApu_IsCritical()
    {
        var result = Eval(new RamSingleRule(), WithModules(Module("A")));

        Assert.Equal(Severity.Critical, result.Finding!.Severity);
        Assert.Equal([ActionIds.HardwareRamDual], result.Finding.ActionIds);
    }

    [Fact]
    public void RamSingle_OneModuleWithDedicatedGpu_IsHigh()
    {
        var snapshot = WithModules(Module("A")) with
        {
            IntegratedGraphicsOnly = Measured<bool>.Of(false),
        };

        Assert.Equal(Severity.High, Eval(new RamSingleRule(), snapshot).Finding!.Severity);
    }

    [Fact]
    public void RamSingle_SolderedLpddr_IsNotApplicable()
    {
        var pc = WithModules(Module("A"));
        var snapshot = pc with { Ram = pc.Ram with { Type = MemoryType.Lpddr } };

        Assert.Equal(RuleOutcome.NotApplicable, Eval(new RamSingleRule(), snapshot).Outcome);
    }

    [Fact]
    public void RamLow_UsesInstalledNotVisibleMemory()
    {
        // 8 GB installed shows as ~7.4 GB with a UMA reservation: still not "low".
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Ram = pc.Ram with
            {
                InstalledBytes = Measured<long>.Of(8 * GiB),
                VisibleBytes = Measured<long>.Of((long)(7.4 * GiB)),
            },
        };

        Assert.Equal(RuleOutcome.Pass, Eval(new RamLowRule(), snapshot).Outcome);
    }

    [Fact]
    public void RamLow_FourGigabytes_IsFound()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Ram = pc.Ram with { InstalledBytes = Measured<long>.Of(4 * GiB) },
        };

        var result = Eval(new RamLowRule(), snapshot);
        Assert.Equal(RuleOutcome.Finding, result.Outcome);
        Assert.Contains(
            result.Finding!.Evidence,
            e => e.Key == "ram.installed_gb" && e.Number == 4
        );
    }

    // ---- R-APU-UMA --------------------------------------------------------------------------

    [Fact]
    public void ApuUma_512MbOn16Gb_IsFound()
    {
        var result = Eval(new ApuUmaRule(), DiegoPc());

        Assert.Equal(RuleOutcome.Finding, result.Outcome);
        Assert.Equal([ActionIds.BiosUma2G], result.Finding!.ActionIds);
        Assert.Contains(
            result.Finding.Evidence,
            e => e.Key == "gpu.dedicated_mb" && e.Number == 512
        );
    }

    [Fact]
    public void ApuUma_2GbAlreadySet_Passes()
    {
        var snapshot = DiegoPc() with { Gpus = [RadeonIgpu(dedicatedMb: 2048)] };
        Assert.Equal(RuleOutcome.Pass, Eval(new ApuUmaRule(), snapshot).Outcome);
    }

    [Fact]
    public void ApuUma_WithDedicatedGpu_IsNotApplicable()
    {
        var snapshot = DiegoPc() with { IntegratedGraphicsOnly = Measured<bool>.Of(false) };
        Assert.Equal(RuleOutcome.NotApplicable, Eval(new ApuUmaRule(), snapshot).Outcome);
    }

    [Fact]
    public void ApuUma_8GbInstalled_Passes()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Ram = pc.Ram with { InstalledBytes = Measured<long>.Of(8 * GiB) },
        };
        Assert.Equal(RuleOutcome.Pass, Eval(new ApuUmaRule(), snapshot).Outcome);
    }

    // ---- Disks ------------------------------------------------------------------------------

    [Theory]
    [InlineData(37, RuleOutcome.Pass, null)]
    [InlineData(12, RuleOutcome.Finding, Severity.Medium)]
    [InlineData(5, RuleOutcome.Finding, Severity.High)]
    public void DiskFree_SeverityFollowsThePlan(
        int freePercent,
        RuleOutcome outcome,
        Severity? severity
    )
    {
        var snapshot = DiegoPc() with
        {
            SystemVolume = new SystemVolumeSnapshot
            {
                TotalBytes = Measured<long>.Of(100 * GiB),
                FreeBytes = Measured<long>.Of(freePercent * GiB),
                Kind = StorageMediaType.Nvme,
            },
        };

        var result = Eval(new DiskFreeRule(), snapshot);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(severity, result.Finding?.Severity);
    }

    [Fact]
    public void DiskFree_StorageSenseAlreadyOn_IsNotRecommended()
    {
        var snapshot = DiegoPc() with
        {
            SystemVolume = new SystemVolumeSnapshot
            {
                TotalBytes = Measured<long>.Of(100 * GiB),
                FreeBytes = Measured<long>.Of(5 * GiB),
            },
        };

        Assert.DoesNotContain(
            ActionIds.StorageSense,
            Eval(new DiskFreeRule(), snapshot).Finding!.ActionIds
        );
    }

    [Fact]
    public void SsdHealth_HealthyDisks_Pass()
    {
        var healthy = DiegoPc() with
        {
            Disks =
            [
                Disk(0, "KINGSTON SA400S37/480GB", StorageMediaType.Ssd),
                Disk(1, "CT500P3PSSD8", StorageMediaType.Nvme),
            ],
        };
        Assert.Equal(RuleOutcome.Pass, Eval(new SsdHealthRule(), healthy).Outcome);
    }

    [Fact]
    public void SsdHealth_DevelopmentPcA400WithOneReadError_IsHigh()
    {
        var result = Eval(new SsdHealthRule(), DiegoPc());
        Assert.Equal(Severity.High, result.Finding!.Severity);
        var fact = Assert.Single(result.Finding.Evidence);
        Assert.Equal("KINGSTON SA400S37/480GB", fact.Subject);
        Assert.Equal(1, fact.Number);
    }

    [Fact]
    public void SsdHealth_UncorrectedErrorsAlone_AreHigh()
    {
        var snapshot = DiegoPc() with
        {
            Disks = [Disk(0, "KINGSTON SA400S37/480GB", StorageMediaType.Ssd, errors: 3)],
        };

        var result = Eval(new SsdHealthRule(), snapshot);
        Assert.Equal(Severity.High, result.Finding!.Severity);
        // A lifetime counter: back up and watch it; replacing is for a disk Windows calls unhealthy.
        Assert.Equal([ActionIds.Backup], result.Finding.ActionIds);
    }

    [Fact]
    public void SsdHealth_UnhealthyDisk_IsCriticalAndRecommendsReplacing()
    {
        var snapshot = DiegoPc() with
        {
            Disks =
            [
                Disk(
                    0,
                    "KINGSTON SA400S37/480GB",
                    StorageMediaType.Ssd,
                    health: DiskHealth.Unhealthy
                ),
            ],
        };

        var result = Eval(new SsdHealthRule(), snapshot);
        Assert.Equal(Severity.Critical, result.Finding!.Severity);
        Assert.Equal([ActionIds.Backup, ActionIds.HardwareReplaceSsd], result.Finding.ActionIds);
    }

    [Fact]
    public void SsdHealth_NothingReadable_IsNotMeasurable()
    {
        var snapshot = DiegoPc() with
        {
            Disks = [Disk(0, "X", StorageMediaType.Ssd, health: null, errors: null)],
        };
        Assert.Equal(RuleOutcome.NotMeasurable, Eval(new SsdHealthRule(), snapshot).Outcome);
    }

    [Fact]
    public void SsdWear_At85Percent_IsCriticalAndNamesTheDisk()
    {
        var snapshot = DiegoPc() with
        {
            Disks =
            [
                Disk(0, "KINGSTON SA400S37/480GB", StorageMediaType.Ssd, wear: 85),
                Disk(1, "CT500P3PSSD8", StorageMediaType.Nvme, wear: 5),
            ],
        };

        var result = Eval(new SsdWearRule(), snapshot);
        Assert.Equal(Severity.Critical, result.Finding!.Severity);
        var fact = Assert.Single(result.Finding.Evidence);
        Assert.Equal("KINGSTON SA400S37/480GB", fact.Subject);
        Assert.Equal(85, fact.Number);
    }

    [Fact]
    public void SsdWear_OneDiskUnreadable_PassesAndListsIt()
    {
        var snapshot = DiegoPc() with
        {
            Disks =
            [
                Disk(0, "KINGSTON SA400S37/480GB", StorageMediaType.Ssd, wear: null),
                Disk(1, "CT500P3PSSD8", StorageMediaType.Nvme, wear: 5),
            ],
        };

        var result = Eval(new SsdWearRule(), snapshot);
        Assert.Equal(RuleOutcome.Pass, result.Outcome);
        Assert.Contains("disk.wear_percent:KINGSTON SA400S37/480GB", result.MissingInputs);
    }

    [Fact]
    public void SsdWear_NoWearAnywhere_IsNotMeasurable()
    {
        var snapshot = DiegoPc() with
        {
            Disks = [Disk(0, "KINGSTON SA400S37/480GB", StorageMediaType.Ssd, wear: null)],
        };
        Assert.Equal(RuleOutcome.NotMeasurable, Eval(new SsdWearRule(), snapshot).Outcome);
    }

    [Fact]
    public void HddSystem_WindowsOnHdd_IsFound()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            SystemVolume = pc.SystemVolume with { Kind = StorageMediaType.Hdd },
        };
        Assert.Equal(RuleOutcome.Finding, Eval(new HddSystemRule(), snapshot).Outcome);
        Assert.Equal(RuleOutcome.Pass, Eval(new HddSystemRule(), pc).Outcome);
    }

    [Fact]
    public void TrimOff_WithSsd_IsFound()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Config = pc.Config with { TrimEnabled = Measured<bool>.Of(false) },
        };
        Assert.Equal(RuleOutcome.Finding, Eval(new TrimOffRule(), snapshot).Outcome);
        Assert.Equal(RuleOutcome.Pass, Eval(new TrimOffRule(), pc).Outcome);
    }

    // ---- Configuration ----------------------------------------------------------------------

    [Theory]
    [InlineData(8, RuleOutcome.Pass)]
    [InlineData(9, RuleOutcome.Finding)]
    public void StartupMany_MoreThanEight_IsFound(int count, RuleOutcome outcome)
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Config = pc.Config with { StartupAppsEnabled = Measured<int>.Of(count) },
        };
        Assert.Equal(outcome, Eval(new StartupManyRule(), snapshot).Outcome);
    }

    [Theory]
    [InlineData(TcpAutoTuning.Disabled, RuleOutcome.Finding)]
    [InlineData(TcpAutoTuning.Normal, RuleOutcome.Pass)]
    public void TcpAutoTune_OnlyDisabledIsFound(TcpAutoTuning level, RuleOutcome outcome)
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Config = pc.Config with { TcpAutoTuning = Measured<TcpAutoTuning>.Of(level) },
        };
        Assert.Equal(outcome, Eval(new TcpAutoTuneRule(), snapshot).Outcome);
    }

    [Fact]
    public void PowerSaver_OnDesktop_IsFound_OnLaptop_NotApplicable()
    {
        var pc = DiegoPc();
        var saver = pc with
        {
            Config = pc.Config with
            {
                PowerPlan = Measured<PowerPlanKind>.Of(PowerPlanKind.PowerSaver),
            },
        };
        var laptop = saver with
        {
            Os = saver.Os with { DeviceKind = Measured<DeviceKind>.Of(DeviceKind.Laptop) },
        };

        Assert.Equal(RuleOutcome.Finding, Eval(new PowerSaverRule(), saver).Outcome);
        Assert.Equal(RuleOutcome.NotApplicable, Eval(new PowerSaverRule(), laptop).Outcome);
        Assert.Equal(RuleOutcome.Pass, Eval(new PowerSaverRule(), pc).Outcome);
    }

    [Fact]
    public void DriverGpuOld_OverTwelveMonths_IsFound()
    {
        var old = RadeonIgpu() with
        {
            DriverDate = Measured<DateOnly>.Of(new DateOnly(2025, 6, 1)),
        };

        var result = Eval(new DriverGpuOldRule(), DiegoPc() with { Gpus = [old] });
        Assert.Equal(RuleOutcome.Finding, result.Outcome);
        Assert.Contains(
            result.Finding!.Evidence,
            e => e.Key == "gpu.driver_age_months" && e.Number == 17
        );
        Assert.Equal(RuleOutcome.Pass, Eval(new DriverGpuOldRule(), DiegoPc()).Outcome);
    }

    // ---- System -----------------------------------------------------------------------------

    [Fact]
    public void BiosOld_LatestInstalled_Passes()
    {
        Assert.Equal(RuleOutcome.Pass, Eval(new BiosOldRule(), DiegoPc()).Outcome);
    }

    [Fact]
    public void BiosOld_OlderVersion_IsLow()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Board = pc.Board with
            {
                BiosVersion = Measured<string>.Of("P4.20"),
                BiosDate = Measured<DateOnly>.Of(new DateOnly(2021, 3, 1)),
            },
        };

        var result = Eval(new BiosOldRule(), snapshot);
        Assert.Equal(Severity.Low, result.Finding!.Severity);
        Assert.Contains(result.Finding.Evidence, e => e.Key == "bios.latest" && e.Text == "P10.10");
    }

    [Fact]
    public void BiosOld_BoardNotInTable_IsNotMeasurable()
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Board = pc.Board with { Product = Measured<string>.Of("B550 AORUS ELITE") },
        };
        var result = Eval(new BiosOldRule(), snapshot);
        Assert.Equal(NotMeasuredReason.NotInReferenceTable, result.Reason);
    }

    [Theory]
    [InlineData(3, 0, RuleOutcome.Finding)]
    [InlineData(2, 0, RuleOutcome.Pass)]
    [InlineData(4, 2, RuleOutcome.Pass)]
    public void Unstable_CountsOnlyUnexpectedShutdowns(int total, int button, RuleOutcome outcome)
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Stability = pc.Stability with
            {
                Kp41Count30d = Measured<int>.Of(total),
                Kp41PowerButton30d = Measured<int>.Of(button),
            },
        };
        Assert.Equal(outcome, Eval(new UnstableRule(), snapshot).Outcome);
    }

    [Theory]
    [InlineData(0, Severity.Medium)] // could be power cuts
    [InlineData(1, Severity.High)] // blue screens: the PC itself
    public void Unstable_IsHighOnlyWithBlueScreens(int bugChecks, Severity expected)
    {
        var pc = DiegoPc();
        var snapshot = pc with
        {
            Stability = pc.Stability with
            {
                Kp41Count30d = Measured<int>.Of(5),
                Kp41PowerButton30d = Measured<int>.Of(0),
                BugChecks30d = Measured<int>.Of(bugChecks),
            },
        };
        Assert.Equal(expected, Eval(new UnstableRule(), snapshot).Finding!.Severity);
    }

    [Fact]
    public void VbsOn_IsInformationOnly()
    {
        var result = Eval(new VbsOnRule(), DiegoPc());
        Assert.Equal(Severity.Info, result.Finding!.Severity);
        Assert.Contains(result.Finding.Evidence, e => e.Key == "os.hvci" && e.Text == "off");
        // Memory Integrity is already off: nothing to turn off (found in the Sandbox tests).
        Assert.Empty(result.Finding.ActionIds);
    }

    [Fact]
    public void VbsOn_WithMemoryIntegrityOn_OffersTheAdvancedGameMode()
    {
        var pc = DiegoPc();
        var snapshot = pc with { Os = pc.Os with { Hvci = Measured<bool>.Of(true) } };
        Assert.Equal(
            [ActionIds.GameModeAdvanced],
            Eval(new VbsOnRule(), snapshot).Finding!.ActionIds
        );
    }

    [Fact]
    public void NotActivated_IsInformationOnlyWithNoAction()
    {
        var pc = DiegoPc();
        var snapshot = pc with { Os = pc.Os with { Activated = Measured<bool>.Of(false) } };

        var result = Eval(new NotActivatedRule(), snapshot);
        Assert.Equal(Severity.Info, result.Finding!.Severity);
        Assert.Empty(result.Finding.ActionIds);
    }

    // ---- Every rule -------------------------------------------------------------------------

    [Fact]
    public void EveryRule_OnAnEmptySnapshot_NeverInventsAFinding()
    {
        foreach (var rule in DiagnosticRules.All)
        {
            var result = Eval(rule, Empty());
            Assert.True(
                result.Outcome is RuleOutcome.NotMeasurable or RuleOutcome.NotApplicable,
                $"{rule.RuleId} returned {result.Outcome} with nothing measured"
            );
        }
    }

    // Version 1.1.2: the graphics driver resets (TDR) seen on Diego's PC.
    [Theory]
    [InlineData(0, null)]
    [InlineData(2, null)]
    [InlineData(3, Severity.Medium)]
    [InlineData(25, Severity.High)]
    public void GpuResets_AreAFindingAfterTwoIn30Days(int resets, Severity? expected)
    {
        var snapshot = DiegoPc() with
        {
            Stability = DiegoPc().Stability with { GpuResets30d = Measured<int>.Of(resets) },
        };

        var result = Eval(new GpuResetsRule(), snapshot);

        Assert.Equal(expected, result.Finding?.Severity);
        if (expected is not null)
            Assert.Equal([ActionIds.DriverGpu, ActionIds.MpoOff], result.Finding!.ActionIds);
    }

    // Version 1.1.2: Windows did not say what the disk is; the measured small-file reads do.
    [Theory]
    [InlineData(180.0, true)] // a 7200 rpm disk
    [InlineData(24000.0, false)] // a SATA SSD
    public void HddSystem_UsesTheMeasuredReads_WhenWindowsDoesNotSay(double iops, bool hdd)
    {
        var snapshot = DiegoPc() with
        {
            SystemVolume = DiegoPc().SystemVolume with
            {
                Kind = StorageMediaType.Unknown,
                RandomReadIops = Measured<double>.Of(iops),
            },
        };

        var result = Eval(new HddSystemRule(), snapshot);

        Assert.Equal(hdd ? RuleOutcome.Finding : RuleOutcome.Pass, result.Outcome);
    }

    [Fact]
    public void HddSystem_WithoutKindOrMeasurement_IsNotMeasurable()
    {
        var snapshot = DiegoPc() with
        {
            SystemVolume = DiegoPc().SystemVolume with { Kind = StorageMediaType.Unknown },
        };
        Assert.Equal(RuleOutcome.NotMeasurable, Eval(new HddSystemRule(), snapshot).Outcome);
    }
}
