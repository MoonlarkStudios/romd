using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public class DatNameParserTests
{
    [Theory]
    [InlineData("Super Mario World (USA)", "USA", null, null)]
    [InlineData("Terranigma (Europe)", "Europe", null, null)]
    [InlineData("Sonic the Hedgehog (World)", "World", null, null)]
    [InlineData("Street Fighter II (Japan) (En)", "Japan", "En", null)]
    [InlineData("Game (US)", "US", null, null)]
    [InlineData("Game (JP)", "JP", null, null)]
    [InlineData("Game (EU)", "EU", null, null)]
    [InlineData("Game (DE)", "DE", null, null)]
    [InlineData("Game (UK)", "UK", null, null)]
    [InlineData("Game (NTSC-U)", "NTSC-U", null, null)]
    [InlineData("Game (PAL)", "PAL", null, null)]
    [InlineData("Game (US, EU)", "US, EU", null, null)]
    [InlineData("Game (United Kingdom)", "United Kingdom", null, null)]
    [InlineData("Game (Hong Kong)", "Hong Kong", null, null)]
    public void Parse_Regions_ExtractsCorrectly(string name, string expectedRegion, string? expectedLang,
        string? expectedRev)
    {
        var result = DatNameParser.Parse(name);

        result.Region.ShouldBe(expectedRegion);
        result.Language.ShouldBe(expectedLang);
        result.Revision.ShouldBe(expectedRev);
    }

    [Theory]
    [InlineData("Super Mario World (USA) (Rev 1)", "Rev 1")]
    [InlineData("Game (USA) (v1.0)", "v1.0")]
    [InlineData("Game (USA) (Beta)", "Beta")]
    [InlineData("Game (USA) (Proto)", "Proto")]
    [InlineData("Game (USA) (Rev A)", "Rev A")]
    public void Parse_Revisions_ExtractsCorrectly(string name, string expectedRev)
    {
        var result = DatNameParser.Parse(name);
        result.Revision.ShouldBe(expectedRev);
    }

    [Theory]
    [InlineData("Ace Combat 3 (Europe) (En,Fr,De,Es,It)", "En,Fr,De,Es,It")]
    [InlineData("Game (Japan) (En)", "En")]
    public void Parse_Languages_ExtractsCorrectly(string name, string expectedLang)
    {
        var result = DatNameParser.Parse(name);
        result.Language.ShouldBe(expectedLang);
    }

    [Fact]
    public void Parse_NoIntroBios_SetsCategoryToBios()
    {
        // Arrange
        string name = "[BIOS] Nintendo 64 - PIF (Brazil)";

        // Act
        var result = DatNameParser.Parse(name);

        // Assert
        result.Category.ShouldBe("BIOS");
        result.Region.ShouldBe("Brazil");
    }

    [Fact]
    public void Parse_ExplicitCategory_OverridesDetection()
    {
        // Arrange
        string name = "Super Mario World (USA)";
        string explicitCategory = "Games"; // e.g. from Redump XML

        // Act
        var result = DatNameParser.Parse(name, explicitCategory);

        // Assert
        result.Category.ShouldBe("Games");
    }

    [Fact]
    public void Parse_GoodDumpTag_SetsVerified()
    {
        var result = DatNameParser.Parse("Super Mario World (USA) [!]");
        result.IsVerified.ShouldBeTrue();
    }

    [Fact]
    public void Parse_NoTags_NotVerified()
    {
        var result = DatNameParser.Parse("Super Mario World (USA)");
        result.IsVerified.ShouldBeFalse();
    }
}
