using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Search.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class AdminCatalogArtworkMappingTests
{
    [Fact]
    public void ToContract_CatalogCard_MapsResolvedArtworkAndPreservesCatalogFacts()
    {
        var asset = new ArtworkAsset(12, 5, ArtworkRole.Poster, "steamgriddb", "game", "asset",
            new ArtworkVariant(1, "original", "image/png", 1200, 1800, "original"),
            [new ArtworkVariant(2, "poster-v1", "image/webp", 600, 900, "poster")], true, DateTimeOffset.UnixEpoch);
        var model = new CatalogTitleData
        {
            Id = 5, PlatformId = 3, Name = "Title", CoverMediaId = 99, HasLocalPayload = true,
            IsTracked = true, LocalPayloadVersionCount = 2, TotalVersionCount = 3,
            Artwork = [ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(5, ArtworkRole.Poster), [asset], [])]
        };

        var contract = model.ToContract(TestSystemCatalog.Keys);

        contract.Artwork.Single().AssetId.ShouldBe(IdCoder.Encode(12));
        contract.Artwork.Single().Url.ShouldBe($"/artwork/{IdCoder.Encode(12)}/poster/poster-v1");
        contract.Artwork.Single().Fit.ShouldBe("Contain");
        contract.Artwork.Single().Width.ShouldBe(600);
        contract.Artwork.Single().OriginalWidth.ShouldBe(1200);
        contract.CoverUrl.ShouldBe($"/media/{IdCoder.Encode(99)}");
        contract.HasLocalPayload.ShouldBeTrue();
        contract.IsTracked.ShouldBeTrue();
        contract.LocalPayloadVersionCount.ShouldBe(2);
        contract.TotalVersionCount.ShouldBe(3);
    }
}
