using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Platform;

public sealed class PlatformAliasTests
{
    [Fact]
    public void CreateName_ValidValue_NormalizesAndSetsType()
    {
        var alias = PlatformAlias.CreateName(5, "  Super  Famicom ");

        alias.PlatformId.ShouldBe(5);
        alias.Type.ShouldBe(PlatformAliasType.Name);
        alias.Value.ShouldBe("Super  Famicom");
        alias.NormalizedValue.ShouldBe("super famicom");
        alias.Provider.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateName_BlankValue_Throws(string value)
    {
        Should.Throw<ArgumentException>(() => PlatformAlias.CreateName(1, value));
    }

    [Fact]
    public void CreateProviderMapping_ValidValues_LowercasesProvider()
    {
        var alias = PlatformAlias.CreateProviderMapping(7, " IGDB ", " 19 ");

        alias.PlatformId.ShouldBe(7);
        alias.Type.ShouldBe(PlatformAliasType.ProviderMapping);
        alias.Provider.ShouldBe("igdb");
        alias.Value.ShouldBe("19");
        alias.NormalizedValue.ShouldBe("19");
    }

    [Fact]
    public void CreateProviderMapping_BlankProvider_Throws()
    {
        Should.Throw<ArgumentException>(() => PlatformAlias.CreateProviderMapping(1, " ", "19"));
    }

    [Fact]
    public void CreateProviderMapping_BlankExternalId_Throws()
    {
        Should.Throw<ArgumentException>(() => PlatformAlias.CreateProviderMapping(1, "igdb", " "));
    }
}

public sealed class PlatformNameNormalizerTests
{
    [Theory]
    [InlineData("SNES", "snes")]
    [InlineData("  Super   Nintendo\tEntertainment  System  ", "super nintendo entertainment system")]
    [InlineData("Sega - Mega Drive - Genesis", "sega - mega drive - genesis")]
    public void Normalize_LowercasesAndCollapsesWhitespace(string input, string expected)
    {
        PlatformNameNormalizer.Normalize(input).ShouldBe(expected);
    }
}
