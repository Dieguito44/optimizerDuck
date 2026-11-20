using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Conditions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Measurements;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Bloatware;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Cli;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;
using optimizerDuck.Services.Optimization;
using optimizerDuck.Services.Profiles;

namespace optimizerDuck.Test.Services.Cli;

/// <summary>Plan of improvements F3-02: the command line and the order of its steps.</summary>
public class CliOptionsTests
{
    private static CliOptions? Parse(params string[] args) => CliOptions.Parse(args, out _);

    [Fact]
    public void FullCommand_IsRead()
    {
        var o = Parse(
            "--preset",
            "estandar",
            "--diagnose",
            "--report",
            @"D:\Clientes\Ana",
            "--client",
            "Ana",
            "--silent",
            "--keep-apps"
        )!;
        Assert.True(o.UsesStandardPreset);
        Assert.True(o.Diagnose);
        Assert.Equal(@"D:\Clientes\Ana", o.ReportDirectory);
        Assert.Equal("Ana", o.Client);
        Assert.True(o.Silent);
        Assert.True(o.KeepApps);
        Assert.False(o.AllowRisky);
    }

    [Theory]
    [InlineData("--diagnose", "--bogus")] // unknown
    [InlineData("--preset")] // no value
    [InlineData("--preset", "--diagnose")] // flag where the value should be
    [InlineData("--report", "x")] // nothing to do
    [InlineData("--diagnose", "--silent")] // --silent only with a preset
    [InlineData("--diagnose", "--allow-risky")]
    [InlineData("--verify", "D:/Ana", "--diagnose")] // --verify goes alone
    [InlineData("--verify")]
    public void BadCommands_AreRefused(params string[] args)
    {
        Assert.Null(CliOptions.Parse(args, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("/?")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_Wins(string arg)
    {
        Assert.True(Parse("--diagnose", arg)!.Help);
    }

    [Fact]
    public void OnlyDashArguments_StartTheCli()
    {
        Assert.False(CliOptions.IsCliInvocation([]));
        Assert.False(CliOptions.IsCliInvocation([@"C:\preset.json"])); // opening a file shows the window
        Assert.True(CliOptions.IsCliInvocation(["--diagnose"]));
        Assert.True(CliOptions.IsCliInvocation(["/?"]));
    }

    [Fact]
    public void ExitCodes_AreStable()
    {
        // Scripts depend on these numbers: changing one breaks them.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7, 10],
            Enum.GetValues<CliExitCode>().Select(c => (int)c).Order()
        );
    }
}

public class CliRunnerTests
{
    private static IOptimization Create(Type t)
    {
        var o = (BaseOptimization)Activator.CreateInstance(t)!;
        o.OwnerType = t.DeclaringType;
        return o;
    }

    private static readonly IReadOnlyList<IOptimization> Catalog = typeof(BaseOptimization)
        .Assembly.GetTypes()
        .Where(t =>
            !t.IsAbstract
            && typeof(BaseOptimization).IsAssignableFrom(t)
            && t.GetCustomAttribute<OptimizationAttribute>() is not null
        )
        .Select(Create)
        .ToList();

    private sealed class Ops(IReadOnlyList<IOptimization> all, List<string> calls)
        : IProfileOperations
    {
        public HashSet<Guid> Failing { get; } = [];

        public Task<IReadOnlyList<IOptimization>> AllOptimizationsAsync() => Task.FromResult(all);

        public Task<bool> IsAppliedAsync(IOptimization o) => Task.FromResult(false);

        public Task<ConditionResult> EvaluateConditionAsync(IOptimization o) =>
            Task.FromResult(ConditionResult.Available);

        public Task<OptimizationSuccessResult> ApplyAsync(
            IOptimization o,
            IProgress<ProcessingProgress> p
        )
        {
            calls.Add("apply");
            return Task.FromResult(
                Failing.Contains(o.Id)
                    ? OptimizationSuccessResult.Failed
                    : OptimizationSuccessResult.Success
            );
        }

        public Task<bool> RevertAsync(IOptimization o, IProgress<ProcessingProgress> p) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<AppXPackage>> InstalledAppsAsync() =>
            Task.FromResult<IReadOnlyList<AppXPackage>>([]);

        public Task<bool> RemoveAppAsync(AppXPackage app) => Task.FromResult(true);

        public Task<IReadOnlyList<PlannedChange>> PreviewAsync(IOptimization o) =>
            Task.FromResult<IReadOnlyList<PlannedChange>>([
                new(PlannedChangeKind.Registry, o.Name, @"HKCU\Test\Value", "1", "0"),
            ]);
    }

    private sealed class Host(List<string> calls) : ICliHost
    {
        public bool IsElevated { get; set; } = true;
        public bool Answer { get; set; } = true;
        public RestorePointResult RestorePoint { get; set; } = RestorePointResult.Success;
        public ServiceSession? Written { get; private set; }
        public string? Transcript { get; private set; }

        public DateTimeOffset Now => new(2026, 11, 19, 15, 0, 0, TimeSpan.Zero);

        public Task<bool> ConfirmAsync(
            ServicePreset preset,
            IReadOnlyList<PlannedChange> changes,
            IReadOnlyList<string> apps
        )
        {
            calls.Add("confirm");
            return Task.FromResult(Answer);
        }

        public Task<RestorePointResult> CreateRestorePointAsync()
        {
            calls.Add("restore-point");
            return Task.FromResult(RestorePoint);
        }

        public Task<(Measurement Measurement, DiagnosisSummary Diagnosis)> MeasureAsync(
            MeasurementPhase phase,
            IProgress<string> progress,
            CancellationToken ct
        )
        {
            calls.Add("measure:" + phase);
            var score = new ScoreBreakdown(
                phase == MeasurementPhase.Before ? 58 : 70,
                0,
                [],
                33,
                0
            );
            return Task.FromResult(
                (
                    new Measurement { Phase = phase, CapturedAtUtc = Now },
                    new DiagnosisSummary(Now, score, [], [])
                )
            );
        }

        public (string Folder, string? Client)? FollowUpDue { get; set; }
        public List<optimizerDuck.Services.FollowUp.FollowUpRun> FollowUpRuns { get; } = [];
        public string? ReportFolder { get; private set; }

        public (string Folder, string? Client)? NextFollowUp(DateTimeOffset nowLocal) =>
            FollowUpDue;

        public Task RecordFollowUpAsync(
            optimizerDuck.Services.FollowUp.FollowUpRun run,
            CancellationToken ct
        )
        {
            calls.Add("followup-recorded");
            FollowUpRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveFollowUpAsync(CancellationToken ct)
        {
            calls.Add("followup-removed");
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> WriteInventoryAsync(
            ServiceSession session,
            CancellationToken ct
        )
        {
            calls.Add("inventory");
            return Task.FromResult<IReadOnlyList<string>>(["inventario.csv"]);
        }

        public IReadOnlyList<string> Extra { get; private set; } = [];

        public Task<IReadOnlyList<string>> WriteReportAsync(
            ServiceSession session,
            string? reportDirectory,
            string transcript,
            IReadOnlyList<string> extraFiles,
            CancellationToken ct
        )
        {
            calls.Add("report");
            Extra = extraFiles;
            ReportFolder = reportDirectory;
            Written = session;
            Transcript = transcript;
            return Task.FromResult<IReadOnlyList<string>>(["session.json"]);
        }
    }

    private sealed record Run(List<string> Calls, Host Host, Ops Ops, CliRunner Runner);

    private static Run NewRun()
    {
        var calls = new List<string>();
        var host = new Host(calls);
        var ops = new Ops(Catalog, calls);
        var runner = new CliRunner(
            new ServiceProfileRunner(ops, NullLogger<ServiceProfileRunner>.Instance),
            host,
            TextWriter.Null,
            NullLogger<CliRunner>.Instance
        );
        return new(calls, host, ops, runner);
    }

    private static Task<CliExitCode> Go(Run run, params string[] args) =>
        run.Runner.RunAsync(CliOptions.Parse(args, out _)!, TestContext.Current.CancellationToken);

    private static string PresetFile(ServicePreset preset)
    {
        var path = Path.Combine(Path.GetTempPath(), $"zentra-preset-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ServicePresetFile.Serialize(preset));
        return path;
    }

    [Fact]
    public async Task NotElevated_DoesNothing()
    {
        var run = NewRun();
        run.Host.IsElevated = false;
        Assert.Equal(CliExitCode.NotElevated, await Go(run, "--preset", "estandar", "--silent"));
        Assert.Empty(run.Calls);
    }

    [Fact]
    public async Task FullRun_FollowsTheSessionOrder()
    {
        var run = NewRun();

        var code = await Go(run, "--preset", "estandar", "--diagnose", "--client", "Ana");

        Assert.Equal(CliExitCode.Ok, code);
        var settings = ServicePreset.Standard.Settings.Count;
        Assert.Equal(
            new[] { "confirm", "restore-point", "measure:Before" }
                .Concat(Enumerable.Repeat("apply", settings))
                .Concat(["measure:After", "report"]),
            run.Calls
        );
        var session = run.Host.Written!;
        Assert.Equal("Servicio estándar", session.PresetName);
        Assert.Equal("Ana", session.ClientLabel);
        Assert.Equal(58, session.DiagnosisBefore!.Score.Score);
        Assert.Equal(70, session.DiagnosisAfter!.Score.Score);
        Assert.Contains(@"HKCU\Test\Value", run.Host.Transcript); // the preview is in the transcript
    }

    [Fact]
    public async Task Silent_SkipsOnlyTheQuestion()
    {
        var run = NewRun();
        Assert.Equal(CliExitCode.Ok, await Go(run, "--preset", "estandar", "--silent"));
        Assert.DoesNotContain("confirm", run.Calls);
        Assert.Equal("restore-point", run.Calls[0]);
    }

    [Fact]
    public async Task SayingNo_AppliesNothing()
    {
        var run = NewRun();
        run.Host.Answer = false;
        Assert.Equal(CliExitCode.Cancelled, await Go(run, "--preset", "estandar"));
        Assert.Equal(["confirm"], run.Calls);
    }

    [Fact]
    public async Task NoRestorePoint_NoChanges()
    {
        var run = NewRun();
        run.Host.RestorePoint = RestorePointResult.Failed;
        Assert.Equal(
            CliExitCode.RestorePointFailed,
            await Go(run, "--preset", "estandar", "--diagnose", "--silent")
        );
        Assert.Equal(["restore-point"], run.Calls);
    }

    [Fact]
    public async Task RecentRestorePoint_IsEnough()
    {
        var run = NewRun();
        run.Host.RestorePoint = RestorePointResult.FrequencyLimitReached;
        Assert.Equal(CliExitCode.Ok, await Go(run, "--preset", "estandar", "--silent"));
        Assert.Contains("apply", run.Calls);
    }

    [Fact]
    public async Task FailedSetting_IsPartial()
    {
        var run = NewRun();
        run.Ops.Failing.Add(ServicePreset.Standard.Settings[0]);
        Assert.Equal(CliExitCode.Partial, await Go(run, "--preset", "estandar", "--silent"));
    }

    [Fact]
    public async Task DiagnoseOnly_ChangesNothing()
    {
        var run = NewRun();
        Assert.Equal(CliExitCode.Ok, await Go(run, "--diagnose"));
        Assert.Equal(["measure:Before", "report"], run.Calls);
        Assert.Null(run.Host.Written!.PresetName);
    }

    [Fact]
    public async Task ExpertSettings_NeedTheFlagToo()
    {
        var risky = Catalog.First(o => o.Risk == OptimizationRisk.Risky);
        var file = PresetFile(
            new ServicePreset
            {
                Name = "Experto",
                AllowRisky = true,
                Settings = [risky.Id],
            }
        );
        try
        {
            var refused = NewRun();
            Assert.Equal(
                CliExitCode.InvalidPreset,
                await Go(refused, "--preset", file, "--silent")
            );
            Assert.Empty(refused.Calls);

            var allowed = NewRun();
            Assert.Equal(
                CliExitCode.Ok,
                await Go(allowed, "--preset", file, "--silent", "--allow-risky")
            );
            Assert.Contains("apply", allowed.Calls);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task MissingOrInvalidPreset_DoesNothing()
    {
        var run = NewRun();
        Assert.Equal(
            CliExitCode.InvalidPreset,
            await Go(run, "--preset", @"C:\no-existe\preset.json", "--silent")
        );

        var file = Path.Combine(Path.GetTempPath(), $"zentra-preset-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            file,
            "{ \"schema\": 1, \"name\": \"x\", \"settings\": [], \"run\": \"cmd\" }"
        );
        try
        {
            Assert.Equal(CliExitCode.InvalidPreset, await Go(run, "--preset", file, "--silent"));
        }
        finally
        {
            File.Delete(file);
        }
        Assert.Empty(run.Calls);
    }

    [Fact]
    public void InventoryAlone_AndVerifyAlone_AreValid()
    {
        Assert.True(CliOptions.Parse(["--inventory"], out _)!.Inventory);
        Assert.Equal(@"D:\Ana", CliOptions.Parse(["--verify", @"D:\Ana"], out _)!.VerifyPath);
    }

    [Fact]
    public async Task Inventory_IsWrittenAndCopiedWithTheReport()
    {
        var run = NewRun();
        Assert.Equal(CliExitCode.Ok, await Go(run, "--diagnose", "--inventory"));
        Assert.Equal(["measure:Before", "inventory", "report"], run.Calls);
        Assert.Equal(["inventario.csv"], run.Host.Extra);
    }

    [Fact]
    public async Task Verify_ChecksTheFolder_WithoutTouchingThePc()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"zentra-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var pdf = Path.Combine(folder, "Informe.pdf");
            File.WriteAllText(pdf, "%PDF");
            optimizerDuck.Services.Reports.ReportIntegrity.WriteManifest(folder, [pdf]);

            var run = NewRun();
            run.Host.IsElevated = false; // checking a report needs no administrator
            Assert.Equal(CliExitCode.Ok, await Go(run, "--verify", folder));

            File.AppendAllText(pdf, "changed");
            Assert.Equal(CliExitCode.VerificationFailed, await Go(run, "--verify", folder));
            Assert.Empty(run.Calls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- Plan of improvements F3-05: follow-up --------------------------------------------------

    [Theory]
    [InlineData("--followup")]
    [InlineData("--remove-followup")]
    public void FollowUpFlags_GoAlone(string flag)
    {
        Assert.NotNull(CliOptions.Parse([flag], out _));
        Assert.Null(CliOptions.Parse([flag, "--diagnose"], out _));
        Assert.Null(CliOptions.Parse(["--followup", "--remove-followup"], out _));
    }

    [Fact]
    public async Task FollowUp_OnlyDiagnoses_IntoTheFollowUpFolder_AndIsRecorded()
    {
        var run = NewRun();
        run.Host.FollowUpDue = (@"C:\ProgramData\Zentra\Seguimiento\7-dias_x", "Ana");

        Assert.Equal(CliExitCode.Ok, await Go(run, "--followup"));

        Assert.Equal(["measure:Before", "report", "followup-recorded"], run.Calls); // no preset, no restore point
        Assert.Equal(@"C:\ProgramData\Zentra\Seguimiento\7-dias_x", run.Host.ReportFolder);
        Assert.Equal("Ana", run.Host.Written!.ClientLabel);
        Assert.Equal(58, Assert.Single(run.Host.FollowUpRuns).Score);
    }

    [Fact]
    public async Task FollowUp_NothingDue_RemovesTheTask()
    {
        var run = NewRun();
        Assert.Equal(CliExitCode.Ok, await Go(run, "--followup"));
        Assert.Equal(["followup-removed"], run.Calls);
    }

    [Fact]
    public async Task RemoveFollowUp_NeedsAdministrator()
    {
        var run = NewRun();
        run.Host.IsElevated = false;
        Assert.Equal(CliExitCode.NotElevated, await Go(run, "--remove-followup"));
        Assert.Empty(run.Calls);

        var admin = NewRun();
        Assert.Equal(CliExitCode.Ok, await Go(admin, "--remove-followup"));
        Assert.Equal(["followup-removed"], admin.Calls);
    }
}
