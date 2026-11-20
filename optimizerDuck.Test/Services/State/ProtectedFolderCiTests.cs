using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using optimizerDuck.Common.Helpers;

namespace optimizerDuck.Test.Services.StateTests;

/// <summary>
///     Plan of improvements F1-02: the protected folder made for real, as administrator, in
///     %ProgramData%. Only on the GitHub runner (elevated and thrown away); locally the suite keeps
///     its state in a temporary folder and never touches ProgramData.
/// </summary>
public class ProtectedFolderCiTests
{
    private static bool OnCi =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && Environment.IsPrivilegedProcess;

    [Fact]
    public void AsAdministrator_TheFolderEndsOwnedByAdministrators_AndUsersCannotWrite()
    {
        if (!OnCi)
            return;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            $"ZentraCiTest-{Guid.NewGuid():N}"
        );
        var state = Path.Combine(root, "Estado");
        try
        {
            Assert.True(ProtectedFolder.Ensure(usersCanRead: true, root, state));

            // The outer folder too: %ProgramData% gives Users "Write" and it would be inherited.
            var rootAcl = new DirectoryInfo(root).GetAccessControl();
            Assert.True(rootAcl.AreAccessRulesProtected);
            Assert.All(
                rootAcl
                    .GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .Where(r =>
                        ((SecurityIdentifier)r.IdentityReference).IsWellKnown(
                            WellKnownSidType.BuiltinUsersSid
                        )
                    ),
                r => Assert.Equal(0, (int)(r.FileSystemRights & FileSystemRights.Write))
            );

            var acl = new DirectoryInfo(state).GetAccessControl();
            Assert.True(
                ((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))!).IsWellKnown(
                    WellKnownSidType.BuiltinAdministratorsSid
                )
            );
            Assert.True(acl.AreAccessRulesProtected);
            var users = acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(r =>
                    ((SecurityIdentifier)r.IdentityReference).IsWellKnown(
                        WellKnownSidType.BuiltinUsersSid
                    )
                )
                .ToList();
            Assert.All(
                users,
                r => Assert.Equal(0, (int)(r.FileSystemRights & FileSystemRights.Write))
            );

            // A second start finds the folder it made and accepts it.
            Assert.True(ProtectedFolder.Ensure(usersCanRead: true, root, state));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
