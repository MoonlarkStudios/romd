using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ArtworkAssetTests
{
    [Fact]
    public void Constructor_EligibleWithoutDeliveryVariant_RejectsAsset()
    {
        Should.Throw<ArgumentException>(() => Asset([], true));
    }

    [Fact]
    public void Constructor_IneligibleWithoutDeliveryVariant_AllowsStagedAsset()
    {
        var asset = Asset([], false);

        asset.IsEligible.ShouldBeFalse();
        asset.Variants.ShouldBeEmpty();
        ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(1, ArtworkRole.Poster), [asset], [])
            .FallbackReason.ShouldBe(ArtworkFallbackReason.NoArtwork);
    }

    [Theory]
    [InlineData("card", "card")]
    [InlineData("card", "CARD")]
    [InlineData("original", "card")]
    [InlineData("ORIGINAL", "card")]
    public void Constructor_AmbiguousVariantNames_RejectsAsset(string first, string second)
    {
        Should.Throw<ArgumentException>(() => Asset([Variant(first), Variant(second)], true));
    }

    [Fact]
    public void Constructor_NullVariantEntry_RejectsAsset()
    {
        Should.Throw<ArgumentException>(() => Asset([null!], true));
    }

    [Theory]
    [InlineData(0, 900)]
    [InlineData(-1, 900)]
    [InlineData(600, 0)]
    [InlineData(600, -1)]
    public void VariantConstructor_InvalidDimensions_RejectsMetadata(int width, int height)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new ArtworkVariant(1, "hash-original-v1", "image/png", width, height, "original"));
    }

    [Fact]
    public void Constructor_ReadyAssetWithoutSpecificDeliverySize_AcceptsUsableVariant()
    {
        var asset = Asset([new ArtworkVariant(2, "hash-small-v1", "image/webp", 200, 300, "small")], true);

        asset.IsEligible.ShouldBeTrue();
        asset.Variants.Single().Width.ShouldBe(200);
    }

    [Theory]
    [InlineData("../card")]
    [InlineData("card/name")]
    [InlineData("card%2fname")]
    [InlineData("card name")]
    [InlineData("..")]
    public void VariantConstructor_UnsafeDeliveryIdentity_IsRejected(string identity)
    {
        Should.Throw<ArgumentException>(() => new ArtworkVariant(1, identity, "image/png", 10, 10, "card"));
        Should.Throw<ArgumentException>(() => new ArtworkVariant(1, "v1", "image/png", 10, 10, identity));
    }

    private static ArtworkAsset Asset(IEnumerable<ArtworkVariant> variants, bool eligible) =>
        new(1, 1, ArtworkRole.Poster, "user", null, null, Variant("original"),
            variants, eligible, DateTimeOffset.UnixEpoch);

    private static ArtworkVariant Variant(string name) =>
        new(1, $"hash-{name}-v1", "image/png", 600, 900, name);
}
