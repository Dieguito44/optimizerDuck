using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using optimizerDuck.Services.System;
using optimizerDuck.Services.UI;
using Serilog.Events;

namespace optimizerDuck.Test.Services.Hardening;

/// <summary>Plan of improvements F1-05: external processes only from Windows' own folders.</summary>
public partial class SystemToolsTests
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    [Theory]
    [InlineData("cmd.exe", "cmd.exe")]
    [InlineData("cmd", "cmd.exe")]
    [InlineData("dism.exe", "dism.exe")]
    [InlineData("sfc.exe", "sfc.exe")]
    [InlineData("chkdsk.exe", "chkdsk.exe")]
    [InlineData("taskmgr", "taskmgr.exe")]
    [InlineData("gpupdate.exe", "gpupdate.exe")] // not on the list, but in System32 (CI found it)
    [InlineData("netsh", "netsh.exe")]
    public void WindowsTools_ResolveToSystem32(string name, string file)
    {
        var path = SystemTools.Resolve(name);
        Assert.Equal(Path.Combine(System32, file), path, ignoreCase: true);
        Assert.True(File.Exists(path), path);
    }

    [Fact]
    public void PowerShellAndExplorer_ResolveToTheirWindowsFolders()
    {
        Assert.True(File.Exists(SystemTools.Resolve("powershell.exe")));
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe"
            ),
            SystemTools.Resolve("explorer"),
            ignoreCase: true
        );
    }

    [Fact]
    public void AbsolutePaths_AreKept_UnknownNames_AreRefused()
    {
        Assert.Equal(@"C:\Tools\x.exe", SystemTools.Resolve(@"C:\Tools\x.exe"));
        // A bare unknown name would have been searched in the PATH: refused instead.
        Assert.Throws<global::System.ComponentModel.Win32Exception>(() =>
            SystemTools.Resolve("notepad++")
        );
        // A relative path is not followed: the name is taken from Windows' own folder.
        Assert.Equal(
            Path.Combine(System32, "cmd.exe"),
            SystemTools.Resolve(@"..\cmd.exe"),
            ignoreCase: true
        );
    }

    [Fact]
    public void Harden_LeavesOnlyWindowsFoldersInThePath()
    {
        var psi = SystemTools.Harden(
            new ProcessStartInfo("cmd.exe", "/c ver") { UseShellExecute = false }
        );

        Assert.Equal(Path.Combine(System32, "cmd.exe"), psi.FileName, ignoreCase: true);
        Assert.Equal(System32, psi.WorkingDirectory, ignoreCase: true);
        var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(
            userFolder,
            psi.Environment["PATH"]!,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            userFolder,
            psi.Environment["PSModulePath"]!,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void Winget_ComesFromItsPackage_SignedByMicrosoft()
    {
        if (SystemTools.Winget() is not { } winget)
            return; // no App Installer (some CI images): nothing to check
        Assert.Contains(
            "Microsoft.DesktopAppInstaller_",
            winget,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            winget,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.True(optimizerDuck.Services.Repair.SafetyScannerService.IsSignedByMicrosoft(winget));
    }

    [GeneratedRegex(
        @"new\s+ProcessStartInfo\s*\(\s*""|FileName\s*=\s*""|ProcessStartInfo\s*\{[^}]*FileName\s*=\s*"""
    )]
    private static partial Regex BareProcessName();

    /// <summary>No process starts from a literal name the PATH would resolve.</summary>
    [Fact]
    public void NoProcess_StartsFromABareName()
    {
        var source = SourceDir();
        var offenders = Directory
            .EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
                !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", ""))
            )
            .SelectMany(f =>
                BareProcessName()
                    .Matches(File.ReadAllText(f))
                    .Select(m => $"{Path.GetFileName(f)}: {m.Value}")
            )
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    internal static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "optimizerDuck", "optimizerDuck.csproj"))
        )
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "optimizerDuck");
    }
}

/// <summary>Plan of improvements F1-06: the cleanup never goes through a junction.</summary>
public sealed class SafeTreeWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"zentra-walk-{Guid.NewGuid():N}"
    );
    private string Inside => Path.Combine(_root, "temp");
    private string Outside => Path.Combine(_root, "outside");

    public SafeTreeWalkTests()
    {
        Directory.CreateDirectory(Path.Combine(Inside, "sub"));
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Inside, "junk.tmp"), "x");
        File.WriteAllText(Path.Combine(Inside, "sub", "junk2.tmp"), "x");
        File.WriteAllText(Path.Combine(Outside, "victim.txt"), "do not delete");
        // A junction needs no special rights (a symbolic link would).
        using var mklink = Process.Start(
            SystemTools.Harden(
                new ProcessStartInfo(
                    "cmd.exe",
                    $"/c mklink /J \"{Path.Combine(Inside, "link")}\" \"{Outside}\""
                )
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                }
            )
        )!;
        mklink.WaitForExit(10_000);
    }

    public void Dispose()
    {
        // Remove the junction first, so the delete never goes through it.
        var link = Path.Combine(Inside, "link");
        if (Directory.Exists(link))
            Directory.Delete(link);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Files_NeverCrossTheJunction_AndCountIt()
    {
        Assert.True(SafeTreeWalk.IsLink(Path.Combine(Inside, "link")));
        var skipped = 0;
        var files = SafeTreeWalk
            .Files(Inside, "*", recurse: true, () => skipped++)
            .Select(f => f.Name)
            .ToList();

        Assert.Equal(["junk.tmp", "junk2.tmp"], files.Order());
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void TheCleanup_DeletesInside_AndLeavesTheOtherSideAlone()
    {
        var service = new DiskCleanupService(NullLogger<DiskCleanupService>.Instance);
        var delete = typeof(DiskCleanupService).GetMethod(
            "DeleteFilesInDirectory",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        delete.Invoke(service, [Inside, "Temp"]);

        Assert.True(File.Exists(Path.Combine(Outside, "victim.txt")));
        Assert.False(File.Exists(Path.Combine(Inside, "junk.tmp")));
        Assert.True(Directory.Exists(Path.Combine(Inside, "link"))); // the link itself is left too
    }

    [Fact]
    public void AFolderThatIsALink_IsNotCleanedAtAll()
    {
        var service = new DiskCleanupService(NullLogger<DiskCleanupService>.Instance);
        var delete = typeof(DiskCleanupService).GetMethod(
            "DeleteFilesInDirectory",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        Assert.Equal(0L, delete.Invoke(service, [Path.Combine(Inside, "link"), "Temp"]));
        Assert.True(File.Exists(Path.Combine(Outside, "victim.txt")));
    }
}

/// <summary>Plan of improvements F1-04: a key that allows setting values but not creating subkeys.</summary>
public class LeastPrivilegeRegistryTests
{
    [Fact]
    public void StartupApproval_WritesWithoutCreateSubKeyRights()
    {
        const string path = @"Software\ZentraTest\SetValueOnly";
        var identity = global::System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var deny = new RegistryAccessRule(
            identity,
            RegistryRights.CreateSubKey,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny
        );
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            var acl = key.GetAccessControl();
            acl.AddAccessRule(deny);
            key.SetAccessControl(acl);
        }
        try
        {
            using (var key = StartupManagerService.OpenForSetValue(Registry.CurrentUser, path))
                key.SetValue("Value", 1);
            Assert.Equal(1, Registry.CurrentUser.OpenSubKey(path)!.GetValue("Value"));
        }
        finally
        {
            using (
                var key = Registry.CurrentUser.OpenSubKey(
                    path,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ReadKey | RegistryRights.ChangePermissions
                )
            )
            {
                var acl = key!.GetAccessControl();
                acl.RemoveAccessRuleSpecific(deny);
                key.SetAccessControl(acl);
            }
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\ZentraTest",
                throwOnMissingSubKey: false
            );
        }
    }
}

/// <summary>Plan of improvements F1-11: logs at Information, rotated, 50 MB at most.</summary>
public sealed class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"zentra-log-{Guid.NewGuid():N}"
    );

    public LoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DefaultLevel_IsInformation_AndRotationCapsAt50MB()
    {
        Assert.Equal(LogEventLevel.Information, App.LogLevel.MinimumLevel);
        Assert.Equal(50L * 1024 * 1024, App.LogFileBytes * App.LogFilesKept);

        using (
            var logger = App.BuildLoggerConfiguration(Path.Combine(_dir, "zentra.log"))
                .CreateLogger()
        )
        {
            logger.Debug("debug line");
            logger.Information("information line");
        }
        var text = string.Concat(Directory.GetFiles(_dir).Select(File.ReadAllText));
        Assert.Contains("information line", text);
        Assert.DoesNotContain("debug line", text);
    }
}
