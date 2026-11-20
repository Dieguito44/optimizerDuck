using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     A folder under %ProgramData% that only SYSTEM and Administrators can write (plan of
///     improvements F1-02 and F3-05). Zentra runs elevated and reads back what it keeps there, so
///     a standard user must not be able to change it. A folder that already exists with another
///     owner, or as a link, is refused rather than taken over.
/// </summary>
public static class ProtectedFolder
{
    /// <summary>
    ///     Creates (or checks) every folder from the outermost to the last, then puts the protected
    ///     ACL on the last one. False when one of them is a link or has an untrusted owner.
    /// </summary>
    public static bool Ensure(bool usersCanRead, params string[] folders)
    {
        foreach (var dir in folders)
        {
            if (Directory.Exists(dir))
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
                if (
                    !IsTrustedOwner(
                        info.GetAccessControl().GetOwner(typeof(SecurityIdentifier))
                            as SecurityIdentifier
                    )
                )
                    return false;
            }
            else
                Directory.CreateDirectory(dir);
        }
        new DirectoryInfo(folders[^1]).SetAccessControl(Acl(usersCanRead));
        return true;
    }

    /// <summary>
    ///     Only the Administrators group (the owner an elevated administrator gives what it
    ///     creates) or SYSTEM.
    /// </summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null
        && (
            owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
            || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
        );

    /// <summary>SYSTEM and Administrators: full control. Users: read and run, or nothing. Nothing inherited.</summary>
    public static DirectorySecurity Acl(bool usersCanRead)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags all =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var rules = new List<(WellKnownSidType, FileSystemRights)>
        {
            (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
        };
        if (usersCanRead)
            rules.Add((WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));
        foreach (var (sid, rights) in rules)
            acl.AddAccessRule(
                new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    rights,
                    all,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
        return acl;
    }
}
