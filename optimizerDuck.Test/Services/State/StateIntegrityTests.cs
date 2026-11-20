using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Revert;
using optimizerDuck.Domain.Revert.Steps;
using optimizerDuck.Domain.UI;
using optimizerDuck.Services.History;
using optimizerDuck.Services.Revert;
using optimizerDuck.Services.State;

namespace optimizerDuck.Test.Services.StateTests;

/// <summary>Plan of improvements F1-02: sealed state files in a protected folder.</summary>
public class StateFileTests
{
    [Fact]
    public void SealedContent_ComesBackUnchanged()
    {
        const string json = "{\"a\": 1,\r\n \"b\": \"ñ\"}";
        Assert.Equal(json, StateFile.Unseal(StateFile.Seal(json)));
    }

    [Theory]
    [InlineData("{\"a\": 1}")] // never sealed (or sealed by someone without the key)
    [InlineData("#zentra-hmac-sha256:zz\n{\"a\": 1}")] // not hex
    [InlineData("#zentra-hmac-sha256:00\n{\"a\": 1}")] // wrong MAC
    [InlineData("")]
    public void UnsealedOrForged_IsRejected(string text)
    {
        Assert.Null(StateFile.Unseal(text));
    }

    [Fact]
    public void AChangedValue_BreaksTheSeal()
    {
        var sealedText = StateFile.Seal("{\"Value\": 0}");
        Assert.Null(StateFile.Unseal(sealedText.Replace("\"Value\": 0", "\"Value\": 1")));
    }

    [Fact]
    public void TheKey_SurvivesARestart()
    {
        var sealedText = StateFile.Seal("{\"x\": true}");
        StateFile.ForgetKey(); // like a new process
        Assert.Equal("{\"x\": true}", StateFile.Unseal(sealedText));
        Assert.True(File.Exists(StateFile.KeyPath));
    }

    [Fact]
    public void TheFolder_OnlyLetsAdministratorsAndSystemWrite()
    {
        foreach (var usersRead in new[] { false, true })
        {
            var acl = ProtectedFolder.Acl(usersRead);
            Assert.True(acl.AreAccessRulesProtected);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();
            Assert.Equal(usersRead ? 3 : 2, rules.Count);
            var writers = rules
                .Where(r => (r.FileSystemRights & FileSystemRights.Write) != 0)
                .Select(r => (SecurityIdentifier)r.IdentityReference)
                .ToList();
            Assert.All(
                writers,
                w =>
                    Assert.True(
                        w.IsWellKnown(WellKnownSidType.LocalSystemSid)
                            || w.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                    )
            );
        }
    }

    [Fact]
    public void TheState_IsInProgramData_NotTheUsersFolder()
    {
        var real = Shared.StateRootOverride;
        Shared.StateRootOverride = null;
        try
        {
            Assert.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Shared.RevertDirectory
            );
            Assert.DoesNotContain(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Shared.HistoryDirectory
            );
        }
        finally
        {
            Shared.StateRootOverride = real;
        }
    }
}

public sealed class StateMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"zentra-migrate-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OldFiles_MoveSealed_AndTheOldFolderGoes()
    {
        var legacy = Path.Combine(_root, "old");
        var target = Path.Combine(_root, "new");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "a.json"), "{\"SchemaVersion\": 1}");
        File.WriteAllText(Path.Combine(legacy, "b.json.unreadable-1"), "broken");

        Assert.Equal(2, StateStore.Migrate(legacy, target));

        Assert.Equal(
            "{\"SchemaVersion\": 1}",
            StateFile.Unseal(File.ReadAllText(Path.Combine(target, "a.json")))
        );
        Assert.Equal("broken", File.ReadAllText(Path.Combine(target, "b.json.unreadable-1")));
        Assert.False(Directory.Exists(legacy));
    }
}

/// <summary>Plan of improvements F1-02: an altered revert file is never reverted.</summary>
public class AlteredStateTests
{
    [Fact]
    public async Task AnAlteredRevertFile_IsUnreadable_NotReverted()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Shared.RevertDirectory, $"{id}.json");
        var data = new RevertData
        {
            SchemaVersion = 1,
            OptimizationId = id,
            OptimizationName = "TestAltered",
            Steps =
            [
                new RevertStepData
                {
                    Index = 1,
                    Type = "Shell",
                    Data = new ShellRevertStep
                    {
                        ShellType = ShellType.CMD,
                        Command = "exit 0",
                    }.ToData(),
                },
            ],
        };
        var sealedText = StateFile.Seal(JsonConvert.SerializeObject(data, Formatting.Indented));
        // Someone without the key changes what would run on revert.
        await File.WriteAllTextAsync(
            path,
            sealedText.Replace("exit 0", "exit 7"),
            TestContext.Current.CancellationToken
        );
        try
        {
            Assert.Null(await RevertManager.GetRevertDataAsync(id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnAlteredChangeRecord_IsIgnored()
    {
        var id = Guid.NewGuid();
        ChangeRecordStore.TryWrite(
            new ChangeRecord
            {
                Id = id,
                LogName = "TestRecord",
                OptimizationKey = "TestRecord",
                AppliedAt = DateTime.UtcNow,
            }
        );
        var path = ChangeRecordStore.PathFor(id);
        Assert.NotNull(ChangeRecordStore.TryRead(id));

        File.WriteAllText(path, File.ReadAllText(path).Replace("TestRecord", "Forged"));
        Assert.Null(ChangeRecordStore.TryRead(id));
        File.Delete(path);
    }
}

/// <summary>Plan of improvements F1-03: the intent journal.</summary>
public class IntentJournalTests
{
    private static Change ShellChange(string command) =>
        new()
        {
            Name = "Shell",
            Description = command,
            Ok = true,
            Revert = new ShellRevertStep { ShellType = ShellType.CMD, Command = command },
        };

    [Fact]
    public void EveryChangeWithAWayBack_IsAnnouncedAsSoonAsItIsRecorded()
    {
        var seen = new List<string>();
        var changes = new ChangeSet { Recorded = c => seen.Add(c.Description) };

        changes.Add(
            "Shell",
            "first",
            true,
            new ShellRevertStep { ShellType = ShellType.CMD, Command = "exit 0" }
        );
        changes.AddSkip("Skip", "nothing to undo");
        changes.Add(
            "Shell",
            "second",
            true,
            new ShellRevertStep { ShellType = ShellType.CMD, Command = "exit 0" }
        );

        Assert.Equal(["first", "second"], seen);
    }

    [Fact]
    public async Task AnUnfinishedRun_BecomesRevertData_AtTheNextStart()
    {
        var id = Guid.NewGuid();
        IntentJournal.Begin(id, "TestUnfinished", DateTimeOffset.UtcNow);
        IntentJournal.Record(id, ShellChange("exit 1"));
        IntentJournal.Record(id, ShellChange("exit 2"));
        // ...and Zentra closes here, before the run's revert data is saved.

        var pending = Assert.Single(IntentJournal.Pending(), e => e.Id == id);
        Assert.Equal(2, pending.Steps.Count);

        var manager = new RevertManager(
            NullLogger<RevertManager>.Instance,
            optimizerDuck.Test.TestDoubles.TestShell.New(),
            new optimizerDuck.Services.System.PowerPlanService(
                NullLogger<optimizerDuck.Services.System.PowerPlanService>.Instance
            ),
            TimeProvider.System
        );
        var recovered = await IntentJournal.RecoverAsync(manager);

        Assert.Contains(recovered, e => e.Id == id);
        Assert.DoesNotContain(IntentJournal.Pending(), e => e.Id == id);
        var data = await RevertManager.GetRevertDataAsync(id);
        Assert.Equal(2, data!.Steps.Count(s => s is not null));
        manager.RemoveRevertData(id);
    }

    [Fact]
    public void AFinishedRun_LeavesNoJournal()
    {
        var id = Guid.NewGuid();
        IntentJournal.Begin(id, "TestFinished", DateTimeOffset.UtcNow);
        IntentJournal.Record(id, ShellChange("exit 0"));
        IntentJournal.Complete(id);
        Assert.DoesNotContain(IntentJournal.Pending(), e => e.Id == id);
    }

    [Fact]
    public void AnAlteredJournal_IsIgnored()
    {
        var id = Guid.NewGuid();
        IntentJournal.Begin(id, "TestForged", DateTimeOffset.UtcNow);
        var path = Path.Combine(Shared.JournalDirectory, $"{id}.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("TestForged", "Evil"));
        Assert.DoesNotContain(IntentJournal.Pending(), e => e.Id == id);
        File.Delete(path);
    }
}
