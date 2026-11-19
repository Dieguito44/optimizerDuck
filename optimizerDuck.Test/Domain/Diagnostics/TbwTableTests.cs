using optimizerDuck.Domain.Diagnostics.Reference;
using optimizerDuck.Services.Diagnostics;

namespace optimizerDuck.Test.Domain.Diagnostics;

/// <summary>Plan of improvements F2-10: the TBW table, every value with its source.</summary>
public class TbwTableTests
{
    private static ReferenceTables Refs => ReferenceTableLoader.Embedded;

    [Fact]
    public void EveryEntry_CitesTheManufacturersDocument()
    {
        Assert.True(Refs.SsdTbw.Count >= 25);
        Assert.All(Refs.SsdTbw, e => Assert.Contains("https://", e.Source));
    }

    [Theory]
    [InlineData("KINGSTON SA400S37/480GB", 160)] // as Diego's PC reports it
    [InlineData("KINGSTON SA400S37480G", 160)] // without the slash
    [InlineData("KINGSTON SA400S37240G", 80)]
    [InlineData("KINGSTON SNV2S1000G", 320)]
    [InlineData("Samsung SSD 870 EVO 1TB", 600)]
    [InlineData("CT480BX500SSD1", 120)] // not 160: the BX500 480GB is rated lower
    [InlineData("CT1000MX500SSD1", 360)]
    [InlineData("CT500P3PSSD8", 110)]
    public void KnownModels_HaveTheirRatedEndurance(string model, int tbw)
    {
        Assert.Equal(tbw, Refs.LookupSsdTbw(model)?.TbwTerabytes);
    }

    [Theory]
    [InlineData("CT1000P3SSD8")] // Crucial P3 (not Plus): no clear official table
    [InlineData("CT4000P3PSSD8")] // P3 Plus 4TB: sources disagree
    [InlineData("WDC WDS500G2B0A")]
    [InlineData("")]
    public void UnknownModels_AreNotGuessed(string model)
    {
        Assert.Null(Refs.LookupSsdTbw(model));
    }
}
