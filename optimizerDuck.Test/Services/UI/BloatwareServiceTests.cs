using optimizerDuck.Domain.Optimizations.Models.Bloatware;
using optimizerDuck.Services.UI;

namespace optimizerDuck.Test.Services.UI;

public class BloatwareServiceTests
{
    [Theory]
    [InlineData("Microsoft.BingNews", "Microsoft.BingNews_4.53.33252.0_x64__8wekyb3d8bbwe", true)]
    [InlineData(
        "Microsoft.WindowsCalculator",
        "Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe",
        true
    )]
    [InlineData("Valid.App-Name_1", "Valid.App-Name_1_1.0.0.0_neutral__8wekyb3d8bbwe", true)]
    [InlineData("Evil$(whoami)", "Evil$(whoami)_1.0.0.0_x64__8wekyb3d8bbwe", false)]
    [InlineData("Evil;rmdir", "Evil;rmdir_1.0.0.0_x64__8wekyb3d8bbwe", false)]
    [InlineData("", "", false)]
    [InlineData("ValidName", "Invalid_Full_Name_Format", false)]
    public void IsValidPackage_ValidatesPackageIdentifiers(
        string name,
        string fullName,
        bool expected
    )
    {
        var package = new AppXPackage
        {
            Name = name,
            PackageFullName = fullName,
            Publisher = "Publisher",
            Version = "1.0.0.0",
            InstallLocation = @"C:\Program Files\WindowsApps\Test",
        };

        var result = BloatwareService.IsValidPackage(package);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParsePackages_SingleObjectJson_ReturnsOnePackage()
    {
        // PowerShell's ConvertTo-Json emits a bare object for one package and an array
        // for several; the single-object shape must not deserialize to an empty list.
        const string json = """
            {"Name":"A.B","PackageFullName":"A.B_1.0.0.0_x64__abc","Publisher":"CN=X","Version":"1.0.0.0","InstallLocation":"","Risk":"Safe"}
            """;

        var packages = BloatwareService.ParsePackages(json);

        var package = Assert.Single(packages);
        Assert.Equal("A.B", package.Name);
        Assert.Equal(AppRisk.Safe, package.Risk);
    }

    [Fact]
    public void ParsePackages_ArrayJson_ReturnsAllPackages()
    {
        const string json = """
            [{"Name":"A.B","PackageFullName":"A.B_1.0.0.0_x64__abc","Publisher":"CN=X","Version":"1.0.0.0","InstallLocation":"","Risk":"Safe"},
             {"Name":"C.D","PackageFullName":"C.D_1.0.0.0_x64__abc","Publisher":"CN=X","Version":"1.0.0.0","InstallLocation":"","Risk":"Caution"}]
            """;

        var packages = BloatwareService.ParsePackages(json);

        Assert.Equal(2, packages.Count);
        Assert.Equal(AppRisk.Caution, packages[1].Risk);
    }

    // Version 1 test (20/11/2026): the Apps page showed raw package names and whole certificate
    // subjects ("CN=Adobe Inc., OU=..., SERIALNUMBER=..."), which made the client's apps hard to tell apart.
    [Theory]
    [InlineData("5319275A.WhatsAppDesktop", "CN=24803D75-212C-471A-BC57-9EF86AB91435", "WhatsAppDesktop", "")]
    [InlineData(
        "AdobeAcrobatDCCoreApp",
        "CN=Adobe Inc., OU=Acrobat DC, O=Adobe Inc., L=San Jose, S=ca, C=US",
        "AdobeAcrobatDCCoreApp",
        "Adobe Inc."
    )]
    [InlineData(
        "Microsoft.BingNews",
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        "Microsoft.BingNews",
        "Microsoft Corporation"
    )]
    [InlineData("Some.App", "CN=Contoso", "Some.App", "Contoso")]
    public void Names_AreShortAndReadable(
        string name,
        string publisher,
        string shortName,
        string publisherName
    )
    {
        var package = new AppXPackage
        {
            Name = name,
            PackageFullName = name + "_1.0.0.0_x64__abc",
            Publisher = publisher,
            Version = "1.0.0.0",
            InstallLocation = @"C:\Program Files\WindowsApps\Test",
        };

        Assert.Equal(shortName, package.ShortName);
        Assert.Equal(publisherName, package.PublisherName);
    }
}
