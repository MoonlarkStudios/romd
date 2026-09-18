using Romd.Admin.Application.Catalog;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public class BiosNormalizerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_WithBlank_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, BiosNormalizer.Normalize(input!));
    }

    [Fact]
    public void Normalize_StripsBiosMarkerAndPunctuation()
    {
        Assert.Equal("sonyplaystationusa", BiosNormalizer.Normalize("[BIOS] Sony PlayStation (USA)"));
    }

    [Fact]
    public void Normalize_IsStableWhenBiosMarkerIsInconsistentAcrossRevisions()
    {
        Assert.Equal(
            BiosNormalizer.Normalize("[BIOS] Sony PlayStation (USA)"),
            BiosNormalizer.Normalize("Sony PlayStation (USA)"));
    }

    [Fact]
    public void Normalize_PreservesRegionDistinction()
    {
        Assert.NotEqual(
            BiosNormalizer.Normalize("[BIOS] Sony PlayStation (USA)"),
            BiosNormalizer.Normalize("[BIOS] Sony PlayStation (Japan)"));
    }

    [Fact]
    public void Normalize_PreservesVersionDistinction()
    {
        Assert.NotEqual(
            BiosNormalizer.Normalize("[BIOS] Sony PlayStation (USA) (v2.0)"),
            BiosNormalizer.Normalize("[BIOS] Sony PlayStation (USA) (v2.2)"));
    }
}
