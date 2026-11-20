using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using optimizerDuck.Services.FollowUp;
using optimizerDuck.Services.Repair;

namespace optimizerDuck.Test.Services.Repair;

/// <summary>Plan of improvements F3-08: the Microsoft Safety Scanner, detect only.</summary>
public class SafetyScannerTests
{
    private const string CleanRun = """
        ---------------------------------------------------------------------------------------
        Microsoft Safety Scanner v1.441, (build 1.441.12.0)
        Started On Thu Nov 19 15:02:11 2026

        Engine: 1.1.25100.2
        Signatures: 1.441.12.0
        Run Mode: Scan Run in Quiet Mode

        Results Summary:
        ----------------
        No infection found.
        Microsoft Safety Scanner Finished On Thu Nov 19 15:09:40 2026

        Return code: 0 (0x0)
        """;

    private const string ThreatRun = """
        ---------------------------------------------------------------------------------------
        Microsoft Safety Scanner v1.441, (build 1.441.12.0)
        Started On Thu Nov 19 16:00:00 2026

        Quick Scan Results:
        -------------------
        Threat Detected: PUA:Win32/Presenoker, not removed.
            file://C:\Users\cliente\Downloads\setup.exe
        Threat Detected: Trojan:Win32/Wacatac.B!ml, not removed.

        Results Summary:
        ----------------
        Found PUA:Win32/Presenoker, not removed.
        Found Trojan:Win32/Wacatac.B!ml, not removed.
        Microsoft Safety Scanner Finished On Thu Nov 19 16:08:00 2026
        """;

    [Fact]
    public void CleanRun_IsClean()
    {
        Assert.Equal(SafetyScanOutcome.Clean, SafetyScannerService.Parse(CleanRun).Outcome);
    }

    [Fact]
    public void Threats_AreNamedOnce_WithoutPaths()
    {
        var (outcome, threats) = SafetyScannerService.Parse(ThreatRun);
        Assert.Equal(SafetyScanOutcome.ThreatsFound, outcome);
        Assert.Equal(["PUA:Win32/Presenoker", "Trojan:Win32/Wacatac.B!ml"], threats);
    }

    [Fact]
    public void AnythingElse_IsUnreadable_NeverClean()
    {
        Assert.Equal(
            SafetyScanOutcome.Unreadable,
            SafetyScannerService.Parse("Microsoft Safety Scanner v1.441\nsomething else").Outcome
        );
    }

    [Fact]
    public void OnlyANewRun_IsRead()
    {
        var log = CleanRun + "\n" + ThreatRun;
        Assert.Null(SafetyScannerService.LastRun(log, runsBefore: 2)); // nothing new
        var last = SafetyScannerService.LastRun(log, runsBefore: 1)!;
        Assert.Contains("Wacatac", last);
        Assert.DoesNotContain("No infection found", last);
    }

    [Theory]
    [InlineData("go.microsoft.com", true)]
    [InlineData("definitionupdates.microsoft.com", true)]
    [InlineData("microsoft.com", true)]
    [InlineData("microsoft.com.evil.example", false)]
    [InlineData("evilmicrosoft.com", false)]
    public void OnlyMicrosoftMayServeTheDownload(string host, bool allowed)
    {
        Assert.Equal(allowed, SafetyScannerService.IsMicrosoftHost(host));
    }

    [Fact]
    public void MicrosoftSignedFiles_Pass_OthersDoNot()
    {
        // dotnet.exe carries an embedded Microsoft signature on every PC that runs these tests.
        var dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe"
        );
        if (File.Exists(dotnet))
            Assert.True(SafetyScannerService.IsSignedByMicrosoft(dotnet));

        var unsigned = Path.Combine(Path.GetTempPath(), $"zentra-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            Assert.False(SafetyScannerService.IsSignedByMicrosoft(unsigned));
        }
        finally
        {
            File.Delete(unsigned);
        }
    }
}

/// <summary>Plan of improvements F3-05: follow-up at 7 and 30 days, diagnosis only.</summary>
public class FollowUpTests
{
    [Fact]
    public void DueDates_AreSevenAndThirtyDaysLater_AtEleven()
    {
        var now = new DateTimeOffset(2026, 11, 19, 15, 30, 0, TimeSpan.FromHours(-5));
        var due = FollowUpService.DueDates(now);
        Assert.Equal(
            [
                new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.FromHours(-5)),
                new DateTimeOffset(2026, 12, 19, 11, 0, 0, TimeSpan.FromHours(-5)),
            ],
            due
        );
    }

    [Fact]
    public void TheTask_RunsAsSystem_OnlyTheFollowUp_WithAProtectedExtractFolder()
    {
        var exe = @"C:\ProgramData\Zentra\Seguimiento\app\Zentra.exe";
        var now = new DateTimeOffset(2026, 11, 19, 15, 30, 0, TimeSpan.FromHours(-5));
        var command = FollowUpService.RegisterCommand(exe, FollowUpService.DueDates(now));

        Assert.Contains("-UserId 'SYSTEM'", command);
        Assert.Contains("-RunLevel Highest", command);
        Assert.Contains("-StartWhenAvailable", command);
        Assert.Contains(
            @"""C:\ProgramData\Zentra\Seguimiento\app\Zentra.exe"" --followup",
            command
        );
        Assert.Contains(
            @"DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\ProgramData\Zentra\Seguimiento\extract",
            command
        );
        Assert.Contains("'2026-11-26 11:00'", command);
        Assert.Contains("'2026-12-19 11:00'", command);
        Assert.DoesNotContain("--preset", command);
        // cmd.exe by its full path, never by name (found in the Sandbox tests, 19/11/2026).
        Assert.Contains(
            $"-Execute '{Path.Combine(Environment.SystemDirectory, "cmd.exe")}'",
            command
        );
    }

    [Theory]
    [InlineData(@"C:\Pro'gramData\Zentra.exe")]
    [InlineData(@"C:\x"" & calc & ""\Zentra.exe")]
    [InlineData(@"C:\%TEMP%\Zentra.exe")]
    public void UnsafePaths_AreRefused(string exe)
    {
        Assert.Throws<ArgumentException>(() => FollowUpService.RegisterCommand(exe, []));
    }

    [Fact]
    public void TheFolder_OnlyLetsAdministratorsAndSystemWrite()
    {
        var acl = FollowUpService.ProtectedAcl();

        Assert.True(acl.AreAccessRulesProtected); // nothing inherited from ProgramData
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            acl.GetOwner(typeof(SecurityIdentifier))
        );
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToDictionary(r => (SecurityIdentifier)r.IdentityReference, r => r.FileSystemRights);

        Assert.Equal(3, rules.Count);
        Assert.Equal(
            FileSystemRights.FullControl,
            rules[new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)]
        );
        Assert.Equal(
            FileSystemRights.FullControl,
            rules[new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]
        );
        var users = rules[new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)];
        Assert.Equal(FileSystemRights.ReadAndExecute, users & ~FileSystemRights.Synchronize);
        Assert.Equal(
            0,
            (int)(
                users
                & (
                    FileSystemRights.Write
                    | FileSystemRights.Delete
                    | FileSystemRights.ChangePermissions
                )
            )
        );
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid, true)]
    [InlineData(WellKnownSidType.LocalSystemSid, true)]
    [InlineData(WellKnownSidType.BuiltinUsersSid, false)]
    [InlineData(WellKnownSidType.WorldSid, false)]
    public void AFolderCreatedByAStandardUser_IsRefused(WellKnownSidType owner, bool trusted)
    {
        Assert.Equal(trusted, FollowUpService.IsTrustedOwner(new SecurityIdentifier(owner, null)));
        Assert.False(
            FollowUpService.IsTrustedOwner(
                new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001")
            )
        );
    }

    [Fact]
    public void State_FinishesAfterEveryDueCheck()
    {
        var state = new FollowUpState { DueAtUtc = [DateTimeOffset.UtcNow, DateTimeOffset.UtcNow] };
        Assert.False(state.IsFinished);
        var one = state with { Runs = [new(DateTimeOffset.UtcNow, "a", 70)] };
        Assert.False(one.IsFinished);
        Assert.True(
            (one with { Runs = [.. one.Runs, new(DateTimeOffset.UtcNow, "b", 72)] }).IsFinished
        );
    }
}
