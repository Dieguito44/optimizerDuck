using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Revert.Steps;
using optimizerDuck.Services.System.Primitives;

namespace optimizerDuck.Test.Services.OptimizationServices;

public class PolicyFileTests
{
    private const string Dsh = @"Software\Policies\Microsoft\Dsh";

    [Fact]
    public void OneDwordEntry_IsTheSame140Bytes_ThatGroupPolicyApplied()
    {
        // The file the feasibility script wrote on Diego's PC, which Windows applied.
        var bytes = PolicyFile.Write([PolicyEntry.Dword(Dsh, "AllowNewsAndInterests", 0)]);

        Assert.Equal(140, bytes.Length);
        Assert.Equal("PReg"u8.ToArray(), bytes[..4]);
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 4));
    }

    [Fact]
    public void Read_GivesBackWhatWasWritten_KeepingOtherEntries()
    {
        PolicyEntry[] entries =
        [
            PolicyEntry.Dword(Dsh, "AllowNewsAndInterests", 0),
            new(
                @"Software\Policies\Vendor",
                "Name",
                1,
                System.Text.Encoding.Unicode.GetBytes("hola\0")
            ),
        ];

        var back = PolicyFile.Read(PolicyFile.Write(entries));

        Assert.Equal(2, back.Count);
        Assert.True(back[0].Matches(Dsh.ToUpperInvariant(), "allownewsandinterests"));
        Assert.Equal(0u, BitConverter.ToUInt32(back[0].Data));
        Assert.Equal(entries[1].Data, back[1].Data);
        Assert.Equal(1u, back[1].Type);
    }

    [Fact]
    public void Read_RejectsAFileThatIsNotPReg()
    {
        Assert.Throws<InvalidDataException>(() => PolicyFile.Read("hello world!"u8.ToArray()));
    }
}

public class GptIniTests
{
    [Fact]
    public void NewFile_GetsTheLinesGpeditWrites()
    {
        var ini = GptIni.Update(null);

        Assert.StartsWith("[General]", ini);
        Assert.Contains("gPCFunctionalityVersion=2", ini);
        Assert.Contains($"gPCMachineExtensionNames={GptIni.RegistryExtensions}", ini);
        Assert.Contains("Version=1", ini);
    }

    [Fact]
    public void ExistingFile_KeepsUserVersionAndOtherExtensions_AndRaisesMachineVersion()
    {
        const string other =
            "[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]";
        var current =
            $"[General]\r\nVersion={(2 << 16) | 5}\r\ngPCMachineExtensionNames={other}\r\n";

        var ini = GptIni.Update(current);

        Assert.Contains($"Version={(2 << 16) | 6}", ini);
        Assert.Contains(GptIni.RegistryExtensions + other, ini);
        Assert.Single(ini.Split("\r\n"), l => l.StartsWith("gPCFunctionalityVersion="));
    }
}

public sealed class LocalGroupPolicyServiceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zentra-gpo-");
    private readonly string _previousDir = LocalGroupPolicyService.PolicyDirectory;
    private readonly Func<ILogger, Task<bool>> _previousRefresh =
        LocalGroupPolicyService.RefreshPolicy;
    private readonly Func<string, string, object?> _previousRead =
        LocalGroupPolicyService.ReadApplied;
    private object? _applied;
    private int _refreshes;

    public LocalGroupPolicyServiceTests()
    {
        LocalGroupPolicyService.PolicyDirectory = _dir.FullName;
        LocalGroupPolicyService.RefreshPolicy = _ =>
        {
            _refreshes++;
            // What the Group Policy service does: apply the file to the registry.
            var pol = Path.Combine(_dir.FullName, "Machine", "Registry.pol");
            _applied = File.Exists(pol)
                ? PolicyFile
                    .Read(File.ReadAllBytes(pol))
                    .Select(e => (object?)BitConverter.ToInt32(e.Data))
                    .FirstOrDefault()
                : null;
            return Task.FromResult(true);
        };
        LocalGroupPolicyService.ReadApplied = (_, _) => _applied;
    }

    public void Dispose()
    {
        LocalGroupPolicyService.PolicyDirectory = _previousDir;
        LocalGroupPolicyService.RefreshPolicy = _previousRefresh;
        LocalGroupPolicyService.ReadApplied = _previousRead;
        _dir.Delete(recursive: true);
    }

    private static OpCall Call() =>
        new() { Changes = new ChangeSet(), Logger = NullLogger.Instance };

    private string Pol => Path.Combine(_dir.FullName, "Machine", "Registry.pol");
    private string Ini => Path.Combine(_dir.FullName, "gpt.ini");

    [Fact]
    public async Task Apply_WritesThePolicy_AndRevert_LeavesNoFilesBehind()
    {
        var call = Call();

        var result = await LocalGroupPolicyService.SetMachineDwordAsync(
            call,
            @"Software\Policies\Microsoft\Dsh",
            "AllowNewsAndInterests",
            0
        );

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, _applied);
        var step = Assert.IsType<GroupPolicyRevertStep>(result.Revert);
        Assert.True(step.CreatedPolicyFile);
        Assert.True(step.CreatedGptIni);

        // The step survives the revert file round trip.
        var restored = GroupPolicyRevertStep.FromData(step.ToData());
        Assert.True(await restored.RestoreAsync(NullLogger.Instance));

        Assert.False(File.Exists(Pol));
        Assert.False(File.Exists(Ini));
        Assert.Null(_applied);
    }

    [Fact]
    public async Task Revert_KeepsOtherPoliciesThatWereAlreadyThere()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pol)!);
        var other = PolicyEntry.Dword(@"Software\Policies\Vendor", "Keep", 7);
        File.WriteAllBytes(Pol, PolicyFile.Write([other]));
        File.WriteAllText(Ini, GptIni.Update(null));

        var result = await LocalGroupPolicyService.SetMachineDwordAsync(
            Call(),
            @"Software\Policies\Microsoft\Dsh",
            "AllowNewsAndInterests",
            0
        );
        var step = Assert.IsType<GroupPolicyRevertStep>(result.Revert);
        Assert.False(step.CreatedPolicyFile);
        Assert.Equal(2, PolicyFile.Read(File.ReadAllBytes(Pol)).Count);

        await step.RestoreAsync(NullLogger.Instance);

        var left = Assert.Single(PolicyFile.Read(File.ReadAllBytes(Pol)));
        Assert.True(left.Matches(other.Key, other.ValueName));
        Assert.True(File.Exists(Ini));
    }

    [Fact]
    public async Task WhenWindowsDoesNotApplyIt_ItFails_AndUndoesTheFiles()
    {
        LocalGroupPolicyService.ReadApplied = (_, _) => null; // e.g. Windows Home

        var result = await LocalGroupPolicyService.SetMachineDwordAsync(
            Call(),
            @"Software\Policies\Microsoft\Dsh",
            "AllowNewsAndInterests",
            0
        );

        Assert.False(result.Ok);
        Assert.False(File.Exists(Pol));
        Assert.False(File.Exists(Ini));
    }

    [Fact]
    public async Task AlreadySetAndApplied_IsASkip_WithoutRefreshing()
    {
        await LocalGroupPolicyService.SetMachineDwordAsync(
            Call(),
            @"Software\Policies\Microsoft\Dsh",
            "AllowNewsAndInterests",
            0
        );
        var before = _refreshes;
        var call = Call();

        await LocalGroupPolicyService.SetMachineDwordAsync(
            call,
            @"Software\Policies\Microsoft\Dsh",
            "AllowNewsAndInterests",
            0
        );

        Assert.Equal(before, _refreshes);
        Assert.Equal(ChangeKind.Skip, Assert.Single(call.Changes.Changes).Kind);
    }
}
