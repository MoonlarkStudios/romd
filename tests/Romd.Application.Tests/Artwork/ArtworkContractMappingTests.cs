using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class ArtworkContractMappingTests
{
    [Fact]
    public void ToContract_RetainedAsset_DefaultUsesLargestDeliveryVariantAndItsDimensions()
    {
        // Arrange
        var original = new ArtworkVariant(1, "original-v1", "image/png", 2400, 3600, "original");
        var small = new ArtworkVariant(2, "small-v1", "image/webp", 300, 450, "small");
        var large = new ArtworkVariant(3, "large-v1", "image/webp", 600, 900, "large");
        var asset = CreateAsset(original, [small, large]);

        // Act
        var contract = Resolve(asset).ToContract();

        // Assert
        contract.Role.ShouldBe("Poster");
        contract.AssetId.ShouldBe(IdCoder.Encode(asset.Id));
        contract.Url.ShouldBe($"/artwork/{IdCoder.Encode(asset.Id)}/large/large-v1");
        contract.ContentVersion.ShouldBe("large-v1");
        contract.Width.ShouldBe(600);
        contract.Height.ShouldBe(900);
        contract.OriginalWidth.ShouldBe(2400);
        contract.OriginalHeight.ShouldBe(3600);
        contract.Fit.ShouldBe("Contain");
        contract.FallbackReason.ShouldBe("None");
        contract.Variants.Select(variant => variant.Name).ShouldBe(["large", "small"]);
        contract.Variants[0].ContentType.ShouldBe("image/webp");
    }

    [Fact]
    public void ToContract_VariantIdentity_RoundTripsInDeliveryPath()
    {
        // Arrange
        var original = new ArtworkVariant(1, "source", "image/png", 2400, 3600, "original");
        var variant = new ArtworkVariant(2, "hash-version_v1", "image/webp", 600, 900, "large-card");

        // Act
        var contract = Resolve(CreateAsset(original, [variant])).ToContract();

        // Assert
        contract.Url.ShouldBe($"/artwork/{IdCoder.Encode(12)}/large-card/hash-version_v1");
        contract.Variants.Single().Url.ShouldBe(contract.Url);
    }

    [Fact]
    public void ToContract_EqualAreaVariants_SelectsStableDefaultRegardlessOfInputOrder()
    {
        // Arrange
        var original = new ArtworkVariant(1, "source", "image/png", 2400, 3600, "original");
        var first = new ArtworkVariant(2, "first-v1", "image/webp", 600, 900, "a");
        var second = new ArtworkVariant(3, "second-v1", "image/webp", 900, 600, "b");

        // Act
        var contract = Resolve(CreateAsset(original, [second, first])).ToContract();
        var reordered = Resolve(CreateAsset(original, [first, second])).ToContract();

        // Assert
        contract.ContentVersion.ShouldBe("first-v1");
        contract.Url.ShouldBe(reordered.Url);
        contract.Width.ShouldBe(600);
        contract.Height.ShouldBe(900);
    }

    [Fact]
    public void ToContract_LegacyCover_PreservesMediaUrlAndUnknownDimensions()
    {
        // Arrange
        var cover = TitleMedia.Rehydrate(17, 5, MediaType.Cover, 2, "igdb", "image/jpeg",
            true, null, DateTimeOffset.UnixEpoch);
        var resolution = ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(5, ArtworkRole.Poster),
            [], [], cover);

        // Act
        var contract = resolution.ToContract();

        // Assert
        contract.Url.ShouldBe($"/media/{IdCoder.Encode(17)}");
        contract.ContentVersion.ShouldBe($"media-{IdCoder.Encode(17)}");
        contract.AssetId.ShouldBeNull();
        contract.Width.ShouldBeNull();
        contract.Height.ShouldBeNull();
        contract.OriginalWidth.ShouldBeNull();
        contract.OriginalHeight.ShouldBeNull();
        contract.Fit.ShouldBe("Contain");
        contract.FallbackReason.ShouldBe("LegacyCover");
        contract.Variants.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ArtworkRole.Hero)]
    [InlineData(ArtworkRole.Logo)]
    public void ToContract_NoArtwork_ReturnsExplicitPlaceholderWithoutFabricatedIdentity(ArtworkRole role)
    {
        // Arrange
        var resolution = ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(5, role), [], []);

        // Act
        var contract = resolution.ToContract();

        // Assert
        contract.Role.ShouldBe(role.ToString());
        contract.Url.ShouldBeNull();
        contract.AssetId.ShouldBeNull();
        contract.ContentVersion.ShouldBeNull();
        contract.Width.ShouldBeNull();
        contract.Height.ShouldBeNull();
        contract.OriginalWidth.ShouldBeNull();
        contract.OriginalHeight.ShouldBeNull();
        contract.Fit.ShouldBe("Contain");
        contract.FallbackReason.ShouldBe("NoArtwork");
        contract.Variants.ShouldBeEmpty();
    }

    private static ArtworkAsset CreateAsset(ArtworkVariant original, IEnumerable<ArtworkVariant> variants) =>
        new(12, 5, ArtworkRole.Poster, "steamgriddb", "game", "asset", original, variants,
            true, DateTimeOffset.UnixEpoch);

    private static ArtworkResolution Resolve(ArtworkAsset asset) =>
        ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(asset.TitleId, asset.Role), [asset], []);
}
