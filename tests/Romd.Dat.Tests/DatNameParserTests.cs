using Shouldly;
using Xunit;

namespace Romd.Dat.Tests;

public class DatNameParserTests
{
    [Theory]
    [InlineData("Donkey Kong Country 2 - Diddy's Kong Quest (USA, Europe) (En,Fr) (Rev 1) (Switch Online)",
        "Donkey Kong Country 2 - Diddy's Kong Quest")]
    [InlineData("Game (Germany) (En,De) (Rev A) (Virtual Console)", "Game")]
    [InlineData("Game (USA, Unrecognized)", "Game (USA, Unrecognized)")]
    [InlineData("Game (Pro) (USA)", "Game (Pro)")]
    [InlineData("Game (USA) (Demo)", "Game (USA) (Demo)")]
    [InlineData("Game (Beta 2) (Rev 1)", "Game (Beta 2)")]
    [InlineData("Game (1995)", "Game (1995)")]
    public void StripReleaseAnnotations_TrailingTags_PreservesEditionText(string name, string expected) =>
        DatNameParser.StripReleaseAnnotations(name).ShouldBe(expected);

    [Theory]
    [InlineData("Game (USA) (Beta 1)", "Game")]
    [InlineData("Game (Japan) (Sample)", "Game")]
    [InlineData("Game (Prototype-2) (Rev 1)", "Game")]
    [InlineData("Game (USA) (The Cowabunga Collection)", "Game (USA) (The Cowabunga Collection)")]
    [InlineData("Game (USA) (Beta Deluxe)", "Game (USA) (Beta Deluxe)")]
    public void StripReleaseAnnotations_NonRetailLookupsOnly_StripsRecognizedTags(string name, string expected) =>
        DatNameParser.StripReleaseAnnotations(name, includeNonRetail: true).ShouldBe(expected);

    [Theory]
    [InlineData("Game (USA) (Beta 1)", true)]
    [InlineData("Game (beta) (Rev 1)", true)]
    [InlineData("Game (Prototype-2)", true)]
    [InlineData("Game (Demo)", true)]
    [InlineData("Game (Sample)", true)]
    [InlineData("Game (USA) (Rev 1)", false)]
    [InlineData("Game (Demolition Edition)", false)]
    [InlineData("Prototype (USA)", false)]
    public void HasNonRetailQualifier_RecognizesTagsIndependentlyOfRevision(string name, bool expected) =>
        DatNameParser.HasNonRetailQualifier(name).ShouldBe(expected);

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
