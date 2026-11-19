using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Execution;
using optimizerDuck.Domain.Optimizations.Models.Services;
using optimizerDuck.Services.System;
using optimizerDuck.Services.System.Primitives;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Test.Services;

/// <summary>
///     Plan of improvements, block 2C: HKCU goes to the person at the PC (F2-05) and links, PDFs
///     and folders open without elevation (F2-06).
/// </summary>
public class UserAndShellTests
{
    private const string TestKey = @"Software\ZentraTests\InteractiveUser";

    // ----- F2-05: HKCU redirection ---------------------------------------------------------

    [Fact]
    public void WithoutRedirection_HkcuIsTheProcessUser()
    {
        Assert.False(InteractiveUser.IsRedirected); // pinned by the test module initializer
        Assert.Equal(string.Empty, BloatwareService.UserArgument());
    }

    [Fact]
    public void Redirected_HkcuWritesAndRevertsLandInTheUsersHive()
    {
        // Redirect to this process's own SID: HKEY_USERS\<sid> is the same hive as HKCU, so the
        // test proves the path is rewritten without touching any other account.
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var previous = InteractiveUser.Source;
        InteractiveUser.Source = () => sid;
        try
        {
            var call = new OpCall { Logger = NullLogger.Instance };
            var item = new RegistryItem($@"HKCU\{TestKey}", "Value", 7);

            Assert.True(RegistryService.Write(call, item).Ok);
            using (var written = Registry.Users.OpenSubKey($@"{sid}\{TestKey}"))
                Assert.Equal(7, written?.GetValue("Value"));

            Assert.True(RegistryService.TryReadValue(item, out var read));
            Assert.Equal(7, read);
        }
        finally
        {
            InteractiveUser.Source = previous;
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\ZentraTests",
                throwOnMissingSubKey: false
            );
        }
    }

    [Fact]
    public void Redirected_BloatwareTargetsTheClientsApps()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var previous = InteractiveUser.Source;
        InteractiveUser.Source = () => sid;
        try
        {
            Assert.Equal($" -User '{sid}'", BloatwareService.UserArgument());
        }
        finally
        {
            InteractiveUser.Source = previous;
        }
    }

    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1001", true)] // local account
    [InlineData("S-1-12-1-1234567890-1234567890-1234567890-1234567890", true)] // Entra ID
    [InlineData("S-1-5-18", false)] // SYSTEM
    [InlineData("S-1-12-1-1'; Remove-Item C:\\", false)] // the Entra prefix alone is not enough
    [InlineData("S-1-5-21-1-2-3-4'; Remove-Item C:\\", false)] // never reaches a command line
    [InlineData("", false)]
    public void OnlyUserSidsAreAccepted(string sid, bool expected)
    {
        Assert.Equal(expected, InteractiveUser.IsUserSid(sid));
    }

    // ----- F2-06: opening without elevation ------------------------------------------------

    [Theory]
    [InlineData("https://github.com/Dieguito44/optimizerDuck/releases/latest")]
    [InlineData(@"C:\Users\cliente\AppData\Local\Zentra\Sessions\2026-11-19_abc\informe.pdf")]
    [InlineData("ms-settings:startupapps")]
    public void UsualTargets_AreAccepted(string target)
    {
        Assert.Equal(target, UnelevatedShell.Validate(target));
    }

    [Theory]
    [InlineData("https://x.com\" /c calc")] // a quote would add arguments to explorer.exe
    [InlineData("C:\\a\nb")]
    [InlineData("")]
    [InlineData("   ")]
    public void TargetsThatWouldBreakTheCommandLine_AreRejected(string target)
    {
        Assert.Throws<ArgumentException>(() => UnelevatedShell.Validate(target));
    }

    [Fact]
    public void Arguments_QuoteTheTarget()
    {
        Assert.Equal(
            "\"C:\\a b\\c.pdf\"",
            UnelevatedShell.Arguments(@"C:\a b\c.pdf", select: false)
        );
        Assert.Equal(
            "/select,\"C:\\a b\\c.pdf\"",
            UnelevatedShell.Arguments(@"C:\a b\c.pdf", select: true)
        );
    }
}
