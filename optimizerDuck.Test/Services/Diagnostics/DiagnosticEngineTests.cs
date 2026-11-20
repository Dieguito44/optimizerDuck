using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Domain.Diagnostics.Rules;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.System;
using optimizerDuck.Test.TestDoubles;
using static optimizerDuck.Test.TestDoubles.SnapshotFactory;

namespace optimizerDuck.Test.Services.Diagnostics;

/// <summary>The closed action catalog against the rules (plan sections 6 and 7).</summary>
public class ActionCatalogTests
{
    [Fact]
    public void Catalog_HoldsThe41ActionsOfThePlan()
    {
        Assert.Equal(50, ActionCatalog.All.Count); // 41 of the plan + 3 of 2B + 3 of 3C + 3 of 4A
        Assert.All(ActionCatalog.All.Keys, id => Assert.Matches(@"^ACT-[A-Z0-9-]+$", id));
    }

    [Fact]
    public void ActionIdsConstants_MatchTheCatalogExactly()
    {
        var constants = typeof(ActionIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Equal(ActionCatalog.All.Keys.ToHashSet(), constants);
    }

    [Fact]
    public void EveryActionARuleMayRecommend_IsInTheCatalog()
    {
        foreach (var rule in DiagnosticRules.All)
            Assert.All(rule.PossibleActionIds, id => Assert.True(ActionCatalog.Contains(id), id));
    }

    [Fact]
    public void EveryRuleDrivenAction_IsUsedByARuleOrPending()
    {
        var used = DiagnosticRules.All.SelectMany(r => r.PossibleActionIds).ToHashSet();
        var ruleDriven = ActionCatalog
            .All.Values.Where(a => a.Origin == ActionOrigin.RuleDriven)
            .Select(a => a.Id);

        Assert.All(
            ruleDriven,
            id =>
                Assert.True(used.Contains(id) || ActionCatalog.PendingRuleActions.Contains(id), id)
        );
    }

    [Fact]
    public void PendingRuleActions_AreNotUsedYet()
    {
        // When a later phase adds the rule, the action must leave the pending list.
        var used = DiagnosticRules.All.SelectMany(r => r.PossibleActionIds).ToHashSet();
        foreach (var pending in ActionCatalog.PendingRuleActions)
            Assert.DoesNotContain(pending, used);
    }

    [Fact]
    public void Rules_AreUniqueAndAllListed()
    {
        var ids = DiagnosticRules.All.Select(r => r.RuleId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches(@"^R-[A-Z0-9-]+$", id));

        var ruleTypes = typeof(IDiagnosticRule)
            .Assembly.GetTypes()
            .Where(t =>
                typeof(IDiagnosticRule).IsAssignableFrom(t)
                && t is { IsClass: true, IsAbstract: false }
            )
            .ToHashSet();
        Assert.Equal(ruleTypes, DiagnosticRules.All.Select(r => r.GetType()).ToHashSet());
        Assert.Equal(39, ids.Count); // 19 of phases 2-4, 10 of phase 6B, 4 of 2B, 3 of 3C, 3 of 4A
    }
}

public class RuleEngineAndScoreTests
{
    private sealed class ThrowingRule : IDiagnosticRule
    {
        public string RuleId => "R-TEST-THROW";
        public IReadOnlyList<string> PossibleActionIds { get; } = [];

        public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class RogueActionRule : IDiagnosticRule
    {
        public string RuleId => "R-TEST-ROGUE";
        public IReadOnlyList<string> PossibleActionIds { get; } = [ActionIds.Trim];

        public RuleResult Evaluate(DiagnosticSnapshot snapshot, ReferenceTables refs) =>
            RuleResult.Found(new Finding(RuleId, Severity.High, [], ["ACT-INVENTED"]));
    }

    [Fact]
    public void Engine_FailingRule_IsNotMeasurableAndOthersStillRun()
    {
        var results = RuleEngine.Evaluate(DiegoPc(), Refs, [new ThrowingRule(), new VbsOnRule()]);

        Assert.Equal(RuleOutcome.NotMeasurable, results[0].Outcome);
        Assert.Equal(NotMeasuredReason.QueryFailed, results[0].Reason);
        Assert.Equal(RuleOutcome.Finding, results[1].Outcome);
    }

    [Fact]
    public void Engine_ActionOutsideTheCatalog_IsRejected()
    {
        var result = Assert.Single(RuleEngine.Evaluate(DiegoPc(), Refs, [new RogueActionRule()]));
        Assert.Null(result.Finding);
    }

    [Theory]
    [InlineData(Severity.Critical, 25)]
    [InlineData(Severity.High, 12)]
    [InlineData(Severity.Medium, 6)]
    [InlineData(Severity.Low, 2)]
    [InlineData(Severity.Info, 0)]
    public void Score_PointsFollowSection71(Severity severity, int points)
    {
        Assert.Equal(points, ScoreCalculator.PointsFor(severity));
    }

    [Fact]
    public void Score_NeverGoesBelowZero()
    {
        var results = Enumerable
            .Range(0, 6)
            .Select(i => RuleResult.Found(new Finding($"R-X{i}", Severity.Critical, [], [])))
            .ToList();

        var score = ScoreCalculator.Calculate(results);
        Assert.Equal(0, score.Score);
        Assert.Equal(150, score.RawDeduction);
    }

    [Fact]
    public void Score_NotMeasurableNeverDeducts()
    {
        var score = ScoreCalculator.Calculate([
            RuleResult.NotMeasurable("R-A", NotMeasuredReason.AccessDenied),
        ]);
        Assert.Equal(100, score.Score);
        Assert.Equal(1, score.RulesNotMeasurable);
    }
}

/// <summary>The development PC, end to end through every rule: the expected diagnosis.</summary>
public class DiegoPcGoldenTests
{
    [Fact]
    public void DiegoPc_FindsUmaDiskErrorsInstabilityAndVbs_Score70()
    {
        var report = DiagnosticReport.Create(DiegoPc(), Refs, DiagnosticRules.All);

        Assert.Equal(
            [RuleIds.ApuUma, RuleIds.SsdHealth, RuleIds.Unstable, RuleIds.VbsOn],
            report.Findings.Select(f => f.RuleId).Order()
        );
        // Plan F2-07: one lifetime disk error is High (not Critical) and shutdowns without blue
        // screens are Medium (could be power cuts).
        Assert.Equal(70, report.Score.Score);
        Assert.Equal(0, report.Score.RulesNotMeasurable);
        Assert.Equal(
            [("R-APU-UMA", 12), ("R-SSD-HEALTH", 12), ("R-UNSTABLE", 6), ("R-VBS-ON", 0)],
            report.Score.Deductions.Select(d => (d.RuleId, d.Points)).OrderBy(d => d.RuleId)
        );
    }

    [Fact]
    public void DiegoPc_WithRamAt2400_AddsXmp()
    {
        var pc = DiegoPc();
        var slow = pc with
        {
            Ram = pc.Ram with
            {
                Modules =
                [
                    Module("A", configured: 2400, reported: 2400),
                    Module("B", configured: 2400, reported: 2400),
                ],
            },
        };

        var report = DiagnosticReport.Create(slow, Refs, DiagnosticRules.All);
        Assert.Contains(report.Findings, f => f.RuleId == RuleIds.RamXmp);
        Assert.Equal(58, report.Score.Score); // 70 minus XMP from the kit table (High, 12)
    }
}

public class ReferenceTablesTests
{
    [Fact]
    public void EmbeddedTables_LoadWithEntriesAndDates()
    {
        var refs = ReferenceTableLoader.Embedded;

        Assert.NotEmpty(refs.RamKits);
        Assert.NotEmpty(refs.SsdTbw);
        Assert.NotEmpty(refs.BiosLatest);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", refs.RamKitsUpdated);
        Assert.All(refs.RamKits, e => Assert.False(string.IsNullOrWhiteSpace(e.Source)));
    }

    [Fact]
    public void EmbeddedTables_KnowTheDevelopmentPc()
    {
        var refs = ReferenceTableLoader.Embedded;

        Assert.Equal(110, refs.LookupSsdTbw("CT500P3PSSD8")?.TbwTerabytes);
        Assert.Equal(160, refs.LookupSsdTbw("KINGSTON SA400S37/480GB")?.TbwTerabytes);
        var bios = refs.LookupBios("ASRock", "B450M-HDV R4.0");
        Assert.Equal("P10.10", bios?.Version);
        Assert.Equal(new DateOnly(2024, 1, 24), bios?.Date);
    }

    [Fact]
    public void Loader_RejectsAnotherSchema()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReferenceTableLoader.Parse("""{ "schema": 2, "entries": [] }""")
        );
    }
}

public class SnapshotBuilderTests
{
    private static SystemInfo Info() =>
        new()
        {
            Windows = new WindowsInfo
            {
                BuildNumber = 26200,
                DisplayVersion = "25H2",
                Edition = WindowsEdition.Pro,
                DeviceKind = DeviceKind.Desktop,
            },
            Cpu = new CpuInfo
            {
                Name = "AMD Ryzen 5 5600GT with Radeon Graphics",
                Vendor = CpuVendor.Amd,
                CoreCount = 6,
                ThreadCount = 12,
            },
            Memory = new MemoryInfo
            {
                TotalBytes = (long)(15.4 * GiB),
                Type = MemoryType.Ddr4,
                Modules =
                [
                    new MemoryModuleInfo
                    {
                        CapacityBytes = 8 * GiB,
                        Slot = "DIMM 0",
                        BankLabel = "P0 CHANNEL A",
                        ConfiguredSpeedMTps = 3600,
                        RatedSpeedMTps = 3600,
                        PartNumber = "  VAM4UX36C188G-DVALWN  ",
                    },
                    new MemoryModuleInfo
                    {
                        CapacityBytes = 8 * GiB,
                        Slot = "DIMM 0",
                        BankLabel = "P0 CHANNEL B",
                        ConfiguredSpeedMTps = 3600,
                        RatedSpeedMTps = 3600,
                    },
                ],
            },
            Gpus =
            [
                new GpuInfo
                {
                    Name = "AMD Radeon(TM) Graphics",
                    Vendor = GpuVendor.Amd,
                    VramMB = 512,
                    DedicatedVramMB = 512,
                    DriverDate = new DateTime(2026, 5, 20),
                },
            ],
            Firmware = new FirmwareInfo
            {
                SmbiosVersion = "P10.10",
                BiosVersion = "ALASKA - 1072009",
                BiosReleaseDateOnly = new DateOnly(2024, 1, 24),
                BoardManufacturer = "ASRock",
                BoardProduct = "B450M-HDV R4.0",
            },
            Security = new SecurityInfo
            {
                VirtualizationBasedSecurity = VbsState.Enabled,
                MemoryIntegrity = VbsState.Disabled,
                SecureBoot = SecureBootState.Enabled,
            },
            Power = new PowerInfo
            {
                SchemeId = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
                SchemeName = "Equilibrado",
            },
            Storage = new StorageInfo
            {
                Volumes =
                [
                    new StorageVolume
                    {
                        DriveLetter = "C:",
                        IsSystemDrive = true,
                        Label = "Windows de Diego",
                        TotalBytes = 464 * GiB,
                        FreeBytes = 173 * GiB,
                        MediaType = StorageMediaType.Nvme,
                    },
                ],
            },
            Runtime = RuntimeInfo.Unknown,
        };

    private static DiagnosticProbeData Probes() =>
        new()
        {
            Disks =
            [
                new DiskProbeRow
                {
                    Number = 0,
                    Model = "KINGSTON SA400S37/480GB",
                    MediaType = 4,
                    BusType = 11,
                    HealthStatus = 0,
                    CountersReason = NotMeasuredReason.AccessDenied,
                },
                new DiskProbeRow
                {
                    Number = 1,
                    Model = "CT500P3PSSD8",
                    MediaType = 4,
                    BusType = 17,
                    HealthStatus = 0,
                    Wear = 5,
                    ReadErrorsUncorrected = 0,
                    WriteErrorsUncorrected = 0,
                },
            ],
            SystemDiskNumber = Measured<int>.Of(1),
            Activated = Measured<bool>.Of(true),
        };

    [Fact]
    public void Build_CarriesSmartctlHostWritesOrTheirReason()
    {
        var probes = Probes();
        probes = probes with
        {
            Disks =
            [
                probes.Disks[0] with
                {
                    HostWritesBytes = 42,
                },
                probes.Disks[1] with
                {
                    HostWritesReason = NotMeasuredReason.AccessDenied,
                },
            ],
        };
        var disks = DiagnosticSnapshotBuilder.Build(Info(), probes, Now).Disks;

        Assert.Equal(42, disks[0].BytesWritten.ValueOrDefault);
        Assert.Equal(NotMeasuredReason.AccessDenied, disks[1].BytesWritten.Reason);
        // Without smartctl's answer the reason stays "needs an external tool".
        var plain = DiagnosticSnapshotBuilder.Build(Info(), Probes(), Now).Disks;
        Assert.Equal(NotMeasuredReason.RequiresExternalTool, plain[0].BytesWritten.Reason);
    }

    [Fact]
    public void Build_UsesInstalledRamAndKeepsSlotsApart()
    {
        var snapshot = DiagnosticSnapshotBuilder.Build(Info(), Probes(), Now);

        Assert.Equal(16 * GiB, snapshot.Ram.InstalledBytes.ValueOrDefault);
        Assert.Equal(
            ["P0 CHANNEL A / DIMM 0", "P0 CHANNEL B / DIMM 0"],
            snapshot.Ram.Modules.Select(m => m.Slot)
        );
        Assert.Equal("VAM4UX36C188G-DVALWN", snapshot.Ram.Modules[0].PartNumber.ValueOrDefault);
        Assert.False(snapshot.Ram.Modules[1].PartNumber.HasValue);
    }

    [Fact]
    public void Build_ReadsSmbiosBiosAndBoardSeparately()
    {
        var board = DiagnosticSnapshotBuilder.Build(Info(), Probes(), Now).Board;

        Assert.Equal("P10.10", board.BiosVersion.ValueOrDefault);
        Assert.Equal(new DateOnly(2024, 1, 24), board.BiosDate.ValueOrDefault);
        Assert.Equal("B450M-HDV R4.0", board.Product.ValueOrDefault);
    }

    [Fact]
    public void Build_SataWearZeroIsUnknown_NvmeWearZeroIsReal()
    {
        // Measured on the development PC: the worn Kingston A400 (SATA) reports Wear = 0.
        var probes = Probes() with
        {
            Disks =
            [
                Probes().Disks[0] with
                {
                    Wear = 0,
                    ReadErrorsUncorrected = 1,
                },
                Probes().Disks[1] with
                {
                    Wear = 0,
                },
            ],
        };

        var disks = DiagnosticSnapshotBuilder.Build(Info(), probes, Now).Disks;

        Assert.Equal(NotMeasuredReason.RequiresExternalTool, disks[0].WearPercent.Reason);
        Assert.False(disks[0].WearPercent.HasValue);
        Assert.Equal(1, disks[0].UncorrectedErrors.ValueOrDefault);
        Assert.Equal(0, disks[1].WearPercent.ValueOrDefault);
        Assert.True(disks[1].WearPercent.HasValue);
    }

    [Fact]
    public void Build_ClassifiesTheApuAndMapsDisks()
    {
        var snapshot = DiagnosticSnapshotBuilder.Build(Info(), Probes(), Now);

        Assert.True(snapshot.IntegratedGraphicsOnly.ValueOrDefault);
        Assert.Equal(512, snapshot.Gpus[0].DedicatedMB.ValueOrDefault);
        Assert.Equal(StorageMediaType.Ssd, snapshot.Disks[0].Kind);
        Assert.Equal(StorageMediaType.Nvme, snapshot.Disks[1].Kind);
        Assert.True(snapshot.Disks[1].IsSystem);
        Assert.Equal(NotMeasuredReason.AccessDenied, snapshot.Disks[0].WearPercent.Reason);
        Assert.Equal(NotMeasuredReason.RequiresExternalTool, snapshot.Disks[1].BytesWritten.Reason);
        Assert.Equal(PowerPlanKind.Balanced, snapshot.Config.PowerPlan.ValueOrDefault);
        Assert.True(snapshot.Os.Vbs.ValueOrDefault);
        Assert.False(snapshot.Os.Hvci.ValueOrDefault);
    }

    [Fact]
    public void Build_UnknownSystemInfo_MeasuresNothingAndDoesNotThrow()
    {
        var snapshot = DiagnosticSnapshotBuilder.Build(
            SystemInfo.Unknown,
            new DiagnosticProbeData(),
            Now
        );

        Assert.False(snapshot.Ram.InstalledBytes.HasValue);
        Assert.False(snapshot.IntegratedGraphicsOnly.HasValue);
        Assert.False(snapshot.Board.BiosVersion.HasValue);
        Assert.False(snapshot.Os.DeviceKind.HasValue);
    }

    [Theory]
    [InlineData("  AMD\tRadeon \n Graphics ", "AMD Radeon Graphics")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Clean_TrimsControlCharactersAndSpaces(string? raw, string? expected)
    {
        Assert.Equal(expected, DiagnosticSnapshotBuilder.Clean(raw));
    }

    [Fact]
    public void Clean_CapsLongText()
    {
        Assert.Equal(64, DiagnosticSnapshotBuilder.Clean(new string('x', 200))!.Length);
    }

    [Fact]
    public void ParseCimDateOnly_KeepsTheFirmwareDate()
    {
        Assert.Equal(
            new DateOnly(2024, 1, 24),
            WmiHelper.ParseCimDateOnly("20240124000000.000000+000")
        );
        Assert.Null(WmiHelper.ParseCimDateOnly("garbage"));
    }

    [Fact]
    public void Privacy_SnapshotJson_HasNoPersonalData()
    {
        var snapshot = DiagnosticSnapshotBuilder.Build(Info(), Probes(), Now);
        var json = JsonConvert.SerializeObject(snapshot);

        Assert.DoesNotContain("Windows de Diego", json); // volume label
        Assert.DoesNotContain("Equilibrado", json); // power plan name
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Privacy_SnapshotTypes_HaveNoPersonalFields()
    {
        var forbidden = new Regex(
            "Serial|User|Machine|Computer|Mac|Ip|Path|Label|Key|Name$",
            RegexOptions.IgnoreCase
        );
        var types = typeof(DiagnosticSnapshot)
            .Assembly.GetTypes()
            .Where(t =>
                t.Namespace == typeof(DiagnosticSnapshot).Namespace && t.Name.EndsWith("Snapshot")
            );

        foreach (var type in types)
        foreach (var property in type.GetProperties())
            Assert.False(
                forbidden.IsMatch(property.Name),
                $"{type.Name}.{property.Name} looks like personal data"
            );
    }
}

public class IntegratedGpuClassifierTests
{
    [Theory]
    [InlineData(
        CpuVendor.Amd,
        "AMD Ryzen 5 5600GT with Radeon Graphics",
        GpuVendor.Amd,
        "AMD Radeon(TM) Graphics",
        true
    )]
    [InlineData(
        CpuVendor.Amd,
        "AMD Ryzen 5 2400G with Radeon Vega Graphics",
        GpuVendor.Amd,
        "AMD Radeon Vega 11 Graphics",
        true
    )]
    [InlineData(
        CpuVendor.Amd,
        "AMD Ryzen 7 7840HS",
        GpuVendor.Amd,
        "AMD Radeon 780M Graphics",
        true
    )]
    [InlineData(CpuVendor.Amd, "AMD Ryzen 5 5600X", GpuVendor.Amd, "AMD Radeon RX 6600", false)]
    [InlineData(
        CpuVendor.Intel,
        "Intel Core i5-12400",
        GpuVendor.Intel,
        "Intel(R) UHD Graphics 730",
        true
    )]
    [InlineData(
        CpuVendor.Intel,
        "Intel Core i7-1165G7",
        GpuVendor.Intel,
        "Intel(R) Iris(R) Xe Graphics",
        true
    )]
    [InlineData(
        CpuVendor.Intel,
        "Intel Core i5-12400F",
        GpuVendor.Intel,
        "Intel(R) Arc(TM) A770 Graphics",
        false
    )]
    [InlineData(
        CpuVendor.Intel,
        "Intel Core i5-12400F",
        GpuVendor.Nvidia,
        "NVIDIA GeForce RTX 3060",
        false
    )]
    [InlineData(
        CpuVendor.Intel,
        "Intel Core i5",
        GpuVendor.Unknown,
        "Microsoft Basic Display Adapter",
        null
    )]
    public void Classify(
        CpuVendor cpuVendor,
        string cpu,
        GpuVendor gpuVendor,
        string gpu,
        bool? expected
    )
    {
        Assert.Equal(expected, IntegratedGpuClassifier.Classify(cpuVendor, cpu, gpuVendor, gpu));
    }

    [Fact]
    public void IntegratedOnly_ApuPlusDedicated_IsFalse()
    {
        GpuSnapshot igpu = RadeonIgpu();
        var dgpu = igpu with { IsIntegrated = Measured<bool>.Of(false) };

        Assert.False(DiagnosticSnapshotBuilder.IntegratedOnly([igpu, dgpu]).ValueOrDefault);
        Assert.True(DiagnosticSnapshotBuilder.IntegratedOnly([igpu]).ValueOrDefault);
        Assert.False(DiagnosticSnapshotBuilder.IntegratedOnly([]).HasValue);
    }
}

/// <summary>Read-only probes on the live PC: they must answer, measured or not, and never throw.</summary>
public class DiagnosticProbesLiveTests
{
    [Fact]
    public void Config_AnswersWithoutThrowing()
    {
        var config = DiagnosticProbes.ReadConfig();
        Assert.True(config.Tcp.HasValue || config.Tcp.Reason != default || !config.Tcp.HasValue);
        Assert.True(config.Trim.HasValue, "TRIM is a plain registry read and should always work");
    }

    [Fact]
    public void Stability_ReadsTheSystemLog()
    {
        var stability = DiagnosticProbes.ReadStability(DateTimeOffset.UtcNow);
        Assert.True(
            stability.Kp41.HasValue,
            $"Kernel-Power 41 not readable: {stability.Kp41.Reason}"
        );
        // Without admin rights Windows denies the Minidump folder: that must read as
        // "access denied", never as zero dumps.
        Assert.True(
            stability.Minidumps.HasValue
                || stability.Minidumps.Reason == NotMeasuredReason.AccessDenied,
            $"Minidumps: {stability.Minidumps}"
        );
    }

    [Theory]
    [InlineData(
        "<Data Name='PowerButtonTimestamp'>0</Data><Data Name='LongPowerButtonPressDetected'>false</Data>",
        false
    )]
    [InlineData("<Data Name='PowerButtonTimestamp'>133912345678</Data>", true)]
    [InlineData("<Data Name=\"LongPowerButtonPressDetected\">true</Data>", true)]
    public void PowerButtonShutdowns_AreToldApart(string xml, bool expected)
    {
        Assert.Equal(expected, DiagnosticProbes.IsPowerButton(xml));
    }
}

/// <summary>Every rule, action, reason and severity has Spanish text: no raw keys on screen.</summary>
public class DiagnosticTranslationTests
{
    private static readonly System.Globalization.CultureInfo Spanish = new("es-ES");

    private static string? Es(string key) =>
        optimizerDuck.Resources.Languages.Translations.ResourceManager.GetString(key, Spanish);

    public static TheoryData<string> Keys()
    {
        var keys = new TheoryData<string>();
        foreach (var rule in DiagnosticRules.All)
        {
            keys.Add($"Diagnostic.Rule.{rule.RuleId}.Title");
            keys.Add($"Diagnostic.Rule.{rule.RuleId}.Description");
        }
        foreach (var action in ActionCatalog.All.Values)
            keys.Add(action.TitleKey);
        foreach (var reason in Enum.GetValues<NotMeasuredReason>())
            keys.Add($"Diagnostic.Reason.{reason}");
        foreach (var severity in Enum.GetValues<Severity>())
            keys.Add($"Diagnostic.Severity.{severity}");
        return keys;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Key_HasSpanishText(string key)
    {
        Assert.False(string.IsNullOrWhiteSpace(Es(key)), key);
    }
}
