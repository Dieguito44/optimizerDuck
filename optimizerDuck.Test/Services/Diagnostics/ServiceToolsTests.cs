using optimizerDuck.Services.Configuration;
using optimizerDuck.Services.Diagnostics;

namespace optimizerDuck.Test.Services.Diagnostics;

/// <summary>Plan of improvements F4-05: crash reports, off by default and without personal data.</summary>
public class CrashReportTests
{
    [Fact]
    public void ReportsAreOff_UntilTheTechnicianTurnsThemOn()
    {
        Assert.False(new optimizerDuck.Domain.Configuration.AppSettings().App.CrashReports);
    }

    [Fact]
    public void TheReport_LosesNamesPathsMailsAddressesAndKeys()
    {
        var text =
            $@"Could not open C:\Users\{Environment.UserName}\Documents\cliente.pdf on {Environment.MachineName}; "
            + @"wrote C:\Users\Ana Pérez\AppData\x; mail diego@example.com; server 192.168.1.20; "
            + "key sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij0123456789-_ABCDEFGHIJKL; "
            + "Windows 10.0.26200.1";

        var redacted = CrashReport.Redact(text);

        Assert.DoesNotContain(Environment.UserName, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.MachineName,
            redacted,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain("Ana Pérez", redacted);
        Assert.DoesNotContain("diego@example.com", redacted);
        Assert.DoesNotContain("192.168.1.20", redacted);
        Assert.DoesNotContain("sk-ant-api03", redacted);
        Assert.Contains(@"C:\Users\<usuario>\", redacted);
        Assert.Contains("10.0.26200.1", redacted); // the Windows version stays
    }

    [Fact]
    public void Disabled_WritesNothing()
    {
        var before = CrashReport.Enabled;
        CrashReport.Enabled = false;
        try
        {
            Assert.Null(CrashReport.TryWrite("Test", new InvalidOperationException("x")));
        }
        finally
        {
            CrashReport.Enabled = before;
        }
    }

    [Fact]
    public void TheReport_HasVersionExceptionAndStack()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }
        var report = CrashReport.Build("App.Startup", ex);
        Assert.Contains("Zentra", report);
        Assert.Contains("[App.Startup]", report);
        Assert.Contains("System.InvalidOperationException: boom", report);
        Assert.Contains(nameof(TheReport_HasVersionExceptionAndStack), report);
    }
}

/// <summary>Plan of improvements F4-06: the speed figures.</summary>
public class SpeedTestTests
{
    [Fact]
    public void Mbps_IsMegabitsPerSecond()
    {
        Assert.Equal(80, SpeedTestService.Mbps(10_000_000, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, SpeedTestService.Mbps(10, TimeSpan.Zero));
    }

    [Fact]
    public void Median_IgnoresFailedRuns_AndIsNullWhenAllFail()
    {
        Assert.Equal(20, SpeedTestService.Median([10, 20, 90]));
        Assert.Equal(15, SpeedTestService.Median([10, 20]));
        Assert.Null(SpeedTestService.Median([]));
    }

    [Fact]
    public async Task FailedRuns_DoNotCount()
    {
        var median = await SpeedTestService.MedianAsync(
            [
                () => Task.FromResult<double?>(null),
                () => Task.FromResult<double?>(50),
                () => Task.FromResult<double?>(70),
            ],
            TestContext.Current.CancellationToken
        );
        Assert.Equal(60, median);
    }
}

/// <summary>Plan of improvements F4-07: warranty lookup.</summary>
public class WarrantyTests
{
    [Theory]
    [InlineData("LENOVO", "20XW", "PF2ABCDE", "https://pcsupport.lenovo.com/warrantylookup")]
    [InlineData("HP", "HP Laptop 15", "5CD1234XYZ", "https://support.hp.com/us-en/check-warranty")]
    [InlineData("Dell Inc.", "Inspiron 15", "ABC1234", "https://www.dell.com/support/home/en-us")]
    [InlineData(
        "ASUSTeK COMPUTER INC.",
        "VivoBook",
        "N1NRKD012345678",
        "https://www.asus.com/support/warranty-status-inquiry/"
    )]
    [InlineData("Micro-Star International", "GF63", "K2012345678", null)] // no known page
    public void KnownBrands_OpenTheirOwnPage(string brand, string model, string serial, string? url)
    {
        var info = WarrantyLookup.Build(brand, model, serial);
        Assert.Equal(serial, info.Serial);
        Assert.Equal(url, info.LookupUrl);
        Assert.DoesNotContain(serial, info.LookupUrl ?? ""); // the serial never goes in the address
    }

    [Theory]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("Default string")]
    [InlineData("System Serial Number")]
    [InlineData("0000000000")]
    [InlineData("123456789")]
    [InlineData("")]
    [InlineData(null)]
    public void PcsBuiltFromParts_HaveNoSerial(string? serial)
    {
        Assert.False(WarrantyLookup.IsRealSerial(serial));
        Assert.Null(
            WarrantyLookup.Build("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", serial).Serial
        );
    }
}
