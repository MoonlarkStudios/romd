using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ArtworkResolverTests
{
    [Fact]
    public void Resolve_UsablePin_WinsOverUserAndProviderPreference()
    {
        var selection = ArtworkSelection.Rehydrate(1, ArtworkRole.Poster, 1,
            ArtworkSelectionMode.Pinned, 3, Guid.NewGuid(), 0, 100);

        var result = ArtworkResolver.Resolve(selection,
            [Asset(1, "user"), Asset(2, "igdb"), Asset(3, "steamgriddb")], ["igdb"]);

        result.Asset!.Id.ShouldBe(3);
        result.Fit.ShouldBe(ArtworkFit.Cover);
        result.FallbackReason.ShouldBe(ArtworkFallbackReason.None);
        result.FocalX.ShouldBe(0);
        result.FocalY.ShouldBe(100);
    }

    [Fact]
    public void Resolve_MissingOrDamagedPin_RendersAutomaticWithoutLosingIntent()
    {
        var selection = ArtworkSelection.Rehydrate(1, ArtworkRole.Poster, 1,
            ArtworkSelectionMode.Pinned, 3, null, 0, 100);

        var result = ArtworkResolver.Resolve(selection,
            [Asset(1, "user"), Asset(3, "steamgriddb", eligible: false)], []);

        result.Asset!.Id.ShouldBe(1);
        result.FocalX.ShouldBe(50);
        result.FocalY.ShouldBe(50);
        selection.PinnedAssetId.ShouldBe(3);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
    }

    [Fact]
    public void Resolve_UserArtwork_WinsOverProviderPreference()
    {
        var result = ArtworkResolver.Resolve(Automatic(),
            [Asset(1, "steamgriddb"), Asset(2, "user")], ["steamgriddb"]);

        result.Asset!.Id.ShouldBe(2);
    }

    [Fact]
    public void Resolve_ProviderPreference_IsIndependentOfInputOrder()
    {
        var assets = new[] { Asset(1, "igdb"), Asset(2, "steamgriddb") };

        ArtworkResolver.Resolve(Automatic(), assets, ["steamgriddb", "igdb"]).Asset!.Id.ShouldBe(2);
        ArtworkResolver.Resolve(Automatic(), assets.Reverse(), ["steamgriddb", "igdb"]).Asset!.Id.ShouldBe(2);
    }

    [Fact]
    public void Resolve_MultipleAssetsFromProvider_UsesOldestThenId()
    {
        var assets = new[] { Asset(1, "steamgriddb", days: 1), Asset(3, "steamgriddb"), Asset(2, "steamgriddb") };

        ArtworkResolver.Resolve(Automatic(), assets, ["steamgriddb"]).Asset!.Id.ShouldBe(2);
        ArtworkResolver.Resolve(Automatic(), assets.Reverse(), ["steamgriddb"]).Asset!.Id.ShouldBe(2);
    }

    [Fact]
    public void Resolve_ProviderRemovedFromPreferences_KeepsRetainedArtworkEligible()
    {
        var result = ArtworkResolver.Resolve(Automatic(), [Asset(1, "steamgriddb")], []);

        result.Asset!.Id.ShouldBe(1);
    }

    [Fact]
    public void Resolve_NoPreferredSources_UsesOrdinalSourceTieBreak()
    {
        var result = ArtworkResolver.Resolve(Automatic(), [Asset(1, "z-provider"), Asset(2, "a-provider")], []);

        result.Asset!.Id.ShouldBe(2);
    }

    [Fact]
    public void Resolve_UnusableAndUnrelatedAssets_AreExcluded()
    {
        var result = ArtworkResolver.Resolve(Automatic(),
            [Asset(1, "user", eligible: false), Asset(2, "user", titleId: 2),
                Asset(3, "user", role: ArtworkRole.Hero), Asset(4, "igdb")], []);

        result.Asset!.Id.ShouldBe(4);
    }

    [Fact]
    public void Resolve_LegacyPosterFallback_PreservesBoxArtAsContainedCover()
    {
        var cover = TitleMedia.CreateNew(1, MediaType.Cover, 42, "igdb", "image/jpeg");

        var result = ArtworkResolver.Resolve(Automatic(), [], [], cover);

        result.Asset.ShouldBeNull();
        result.LegacyCover.ShouldBeSameAs(cover);
        result.Fit.ShouldBe(ArtworkFit.Contain);
        result.FallbackReason.ShouldBe(ArtworkFallbackReason.LegacyCover);
        cover.IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_HeroWithoutArtwork_DoesNotCropHistoricalCover()
    {
        var cover = TitleMedia.CreateNew(1, MediaType.Cover, 42, "igdb", "image/jpeg");

        var result = ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(1, ArtworkRole.Hero), [], [], cover);

        result.Asset.ShouldBeNull();
        result.LegacyCover.ShouldBeNull();
        result.FallbackReason.ShouldBe(ArtworkFallbackReason.NoArtwork);
    }

    [Fact]
    public void Resolve_LogoAutomaticAndPin_AlwaysContainCompleteArtwork()
    {
        var automatic = ArtworkSelection.CreateAutomatic(1, ArtworkRole.Logo);
        var assets = new[]
        {
            Asset(1, "steamgriddb", role: ArtworkRole.Logo),
            Asset(2, "user", role: ArtworkRole.Logo)
        };

        var automaticResult = ArtworkResolver.Resolve(automatic, assets, ["steamgriddb"]);
        automaticResult.Asset!.Id.ShouldBe(2);
        automaticResult.Fit.ShouldBe(ArtworkFit.Contain);

        var pinned = ArtworkSelection.Rehydrate(1, ArtworkRole.Logo, 1,
            ArtworkSelectionMode.Pinned, 1, null, 10, 90);
        var pinnedResult = ArtworkResolver.Resolve(pinned, assets, ["steamgriddb"]);
        pinnedResult.Asset!.Id.ShouldBe(1);
        pinnedResult.Fit.ShouldBe(ArtworkFit.Contain);
        pinnedResult.FocalX.ShouldBe(10);
        pinnedResult.FocalY.ShouldBe(90);
    }

    [Fact]
    public void Resolve_MissingLogo_DoesNotUseHistoricalLogoClassificationAsFallback()
    {
        var legacyLogo = TitleMedia.CreateNew(1, MediaType.Logo, 42, "user", "image/png");

        var result = ArtworkResolver.Resolve(
            ArtworkSelection.CreateAutomatic(1, ArtworkRole.Logo), [], [], legacyLogo);

        result.Asset.ShouldBeNull();
        result.LegacyCover.ShouldBeNull();
        result.FallbackReason.ShouldBe(ArtworkFallbackReason.NoArtwork);
        result.Fit.ShouldBe(ArtworkFit.Contain);
    }

    [Theory]
    [InlineData(2, MediaType.Cover)]
    [InlineData(1, MediaType.Banner)]
    public void Resolve_UnrelatedLegacyMedia_IsNotFallback(int titleId, MediaType type)
    {
        var cover = TitleMedia.CreateNew(titleId, type, 42, "igdb", "image/jpeg");

        ArtworkResolver.Resolve(Automatic(), [], [], cover).FallbackReason.ShouldBe(ArtworkFallbackReason.NoArtwork);
    }

    [Fact]
    public void Asset_MutableVariantInput_DoesNotChangeRetainedMetadata()
    {
        var original = new ArtworkVariant(1, "hash-original-v1", "image/png", 600, 900, "original");
        var variants = new List<ArtworkVariant> { new(2, "hash-poster-v1", "image/webp", 300, 450, "card") };
        var asset = new ArtworkAsset(1, 1, ArtworkRole.Poster, "user", null, null,
            original, variants, true, DateTimeOffset.UnixEpoch);

        variants.Clear();

        asset.Variants.Count.ShouldBe(1);
        asset.Original.ShouldBeSameAs(original);
    }

    private static ArtworkSelection Automatic() => ArtworkSelection.CreateAutomatic(1, ArtworkRole.Poster);

    private static ArtworkAsset Asset(int id, string sourceId, bool eligible = true, int titleId = 1,
        ArtworkRole role = ArtworkRole.Poster, int days = 0) =>
        new(id, titleId, role, sourceId, "game", "asset",
            new ArtworkVariant(id, $"hash-{id}-original-v1", "image/png", 600, 900, "original"),
            [new ArtworkVariant(2, "hash-card-v1", "image/webp", 300, 450, "card")], eligible, DateTimeOffset.UnixEpoch.AddDays(days));
}
