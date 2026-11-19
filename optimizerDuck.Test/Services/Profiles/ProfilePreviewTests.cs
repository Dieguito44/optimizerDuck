using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Categories;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.Profiles;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Test.TestDoubles;

namespace optimizerDuck.Test.Services.Profiles;

/// <summary>
///     Plan of improvements F2-03: the profile preview is a dry run that never changes the PC.
/// </summary>
public class ProfilePreviewTests
{
    private const string TestKey = @"HKCU\Software\ZentraTests\Preview";

    [Fact]
    public void RegistryWrite_InPreview_DescribesAndDoesNotWrite()
    {
        var plan = new PreviewPlan { CurrentSetting = "Test" };
        var call = new OpCall { Logger = NullLogger.Instance, Preview = plan };

        Assert.True(RegistryService.Write(call, new RegistryItem(TestKey, "Value", 1)).Ok);

        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\ZentraTests\Preview"))
            Assert.Null(key); // not even the key was created
        var change = Assert.Single(plan.Items);
        Assert.Equal(PlannedChangeKind.Registry, change.Kind);
        Assert.Null(change.Current); // "(does not exist)"
        Assert.Equal("1", change.Next);
        Assert.Empty(call.Changes.Changes); // nothing recorded for revert
    }

    [Fact]
    public async Task ShellCommand_InPreview_IsNotRun()
    {
        var plan = new PreviewPlan();
        var call = new OpCall { Logger = NullLogger.Instance, Preview = plan };
        var marker = Path.Combine(Path.GetTempPath(), $"zentra-preview-{Guid.NewGuid():N}.txt");

        await TestShell
            .New()
            .CMDAsync($"echo x > \"{marker}\"", call, ct: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(marker));
        Assert.Equal(PlannedChangeKind.Command, Assert.Single(plan.Items).Kind);
    }

    [Fact]
    public void ProtectedTask_InPreview_SaysItStaysUnchanged()
    {
        var plan = new PreviewPlan();
        var call = new OpCall { Logger = NullLogger.Instance, Preview = plan };
        ScheduledTaskService.DisableTask(call, @"\Microsoft\Windows\WindowsUpdate\Scheduled Start");
        Assert.Equal("(protected: unchanged)", Assert.Single(plan.Items).Next);
    }

    [Fact]
    public async Task ARealProfileSetting_InPreview_LeavesTheRegistryAsItWas()
    {
        // Click To Do writes HKCU and HKLM values: compare them before and after a dry run.
        var optimization = new AI.DisableClickToDo { OwnerType = typeof(AI) };
        var plan = new PreviewPlan { CurrentSetting = optimization.Name };
        var context = new OptimizationContext
        {
            Snapshot = SystemInfo.Unknown,
            StreamService = new StreamService(NullLogger<StreamService>.Instance),
            Shell = TestShell.New(),
            PowerPlans = new PowerPlanService(NullLogger<PowerPlanService>.Instance),
            Logger = NullLogger.Instance,
            Preview = plan,
        };

        await optimization.ApplyAsync(new Progress<ProcessingProgress>(), context);

        // Each planned change read the value before the "write"; after the dry run the value
        // is still exactly that one.
        Assert.NotEmpty(plan.Items);
        Assert.All(plan.Items, c => Assert.Equal(PlannedChangeKind.Registry, c.Kind));
        var now = Snapshot(plan);
        Assert.All(plan.Items, c => Assert.Equal(c.Current, now[c.Target]));
        Assert.Empty(context.Changes.Changes);
    }

    /// <summary>The current value of every registry target in the plan.</summary>
    private static Dictionary<string, string?> Snapshot(PreviewPlan plan) =>
        plan
            .Items.Where(c => c.Kind == PlannedChangeKind.Registry)
            .ToDictionary(
                c => c.Target,
                c =>
                {
                    var i = c.Target.LastIndexOf('\\');
                    return
                        RegistryService.TryReadValue(
                            new RegistryItem(c.Target[..i], c.Target[(i + 1)..]),
                            out var v
                        ) && v is not null
                        ? RegistryService.DisplayValue(v)
                        : null;
                }
            );

    /// <summary>
    ///     The preview is only safe while every profile setting changes the PC through the
    ///     guarded primitives. A setting that writes the registry, files or processes directly
    ///     would really change the PC during the preview: this test fails first.
    /// </summary>
    [Fact]
    public void ProfileSettings_OnlyChangeThePcThroughGuardedPrimitives()
    {
        var source = SourceDirectory();
        var forbidden = new Regex(
            @"\bRegistry\.(LocalMachine|CurrentUser|Users|ClassesRoot)\b[^;]*\.(SetValue|CreateSubKey|DeleteValue|DeleteSubKey)"
                + @"|\.SetValue\(|\bFile\.(Write|Delete|Move|Copy|Create)|\bDirectory\.(Delete|Move|Create)"
                + @"|\bProcess\.Start\b|\bnew\s+ProcessStartInfo\b|\bServiceController\b"
                + @"|\bClientConsent\b"
        );

        var offenders = new List<string>();
        foreach (var type in StandardServiceProfile.Settings)
        {
            var file = Path.Combine(
                source,
                "Domain",
                "Optimizations",
                "Categories",
                $"{type.DeclaringType!.Name}.cs"
            );
            var text = File.ReadAllText(file);
            var start = Regex.Match(text, $@"class {type.Name}\b").Index;
            var next = Regex.Match(
                text[(start + 1)..],
                @"\n    (\[Optimization|public (sealed )?class )"
            );
            var body = next.Success ? text.Substring(start, next.Index + 1) : text[start..];
            foreach (Match m in forbidden.Matches(body))
                offenders.Add($"{type.Name}: {m.Value}");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    ///     Since presets (plan F3-01) any optimization can be previewed, not only the profile's.
    ///     Every class that does something outside the guarded primitives (a direct write, a
    ///     process, the consent window) must handle the preview itself, QueryCMDAsync (no dry
    ///     run) must go through PreviewOnly, and QueryPowerShellAsync may only read (Get-...).
    /// </summary>
    [Fact]
    public void EveryOptimization_IsSafeToPreview()
    {
        var source = SourceDirectory();
        var direct = new Regex(
            @"\.SetValue\(|\bFile\.(Write|Delete|Move|Copy|Create)|\bDirectory\.(Delete|Move)"
                + @"|\bProcess\.Start\b|\bnew\s+ProcessStartInfo\b|\bServiceController\b"
                + @"|\bClientConsent\b"
        );
        // The first argument: a literal (captured in cmd) or anything else (a variable).
        var psQuery = new Regex(
            @"QueryPowerShellAsync\(\s*(?://[^\n]*\n\s*)*(?:\$?""(?<cmd>[^""]*)""|(?<expr>[^,)\s]+))"
        );

        var offenders = new List<string>();
        var types = typeof(BaseOptimization)
            .Assembly.GetTypes()
            .Where(t =>
                !t.IsAbstract
                && typeof(BaseOptimization).IsAssignableFrom(t)
                && t.GetCustomAttribute<OptimizationAttribute>() is not null
                && t.DeclaringType is not null
            );
        foreach (var type in types)
        {
            var file = Path.Combine(
                source,
                "Domain",
                "Optimizations",
                "Categories",
                $"{type.DeclaringType!.Name}.cs"
            );
            var text = File.ReadAllText(file);
            var start = Regex.Match(text, $@"class {type.Name}\b").Index;
            var next = Regex.Match(
                text[(start + 1)..],
                @"\n    (\[Optimization|public (sealed )?class )"
            );
            var body = next.Success ? text.Substring(start, next.Index + 1) : text[start..];

            var handlesPreview = body.Contains("context.Preview") || body.Contains("PreviewOnly(");
            if (!handlesPreview)
                foreach (Match m in direct.Matches(body))
                    offenders.Add($"{type.Name}: {m.Value} without a preview guard");
            if (body.Contains("QueryCMDAsync(") && !body.Contains("PreviewOnly("))
                offenders.Add($"{type.Name}: QueryCMDAsync without PreviewOnly");
            foreach (Match m in psQuery.Matches(body))
            {
                // A literal Get- only reads. Anything else may write and needs PreviewOnly.
                var cmd = m.Groups["cmd"].Value.TrimStart('(', '[', 'b', 'o', 'l', ']');
                var readsOnly =
                    m.Groups["cmd"].Success && cmd.StartsWith("Get-", StringComparison.Ordinal);
                if (!readsOnly && !body.Contains("PreviewOnly("))
                    offenders.Add($"{type.Name}: QueryPowerShellAsync that may write: {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static string SourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "optimizerDuck", "optimizerDuck.csproj"))
        )
            dir = dir.Parent;
        return dir is null
            ? throw new DirectoryNotFoundException("Could not locate the app source directory.")
            : Path.Combine(dir.FullName, "optimizerDuck");
    }
}
