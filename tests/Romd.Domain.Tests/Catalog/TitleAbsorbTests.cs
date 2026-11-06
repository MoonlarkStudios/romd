using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleAbsorbTests
{
    [Fact]
    public void Absorb_ProviderUnlinkedOnTheTarget_AbsorbsNeitherItsLinkNorAnyFieldNorMediaFromIt()
    {
        var target = Title(1);
        var source = Title(2,
            [TitleExternalId.CreateNew(2, "screenscraper", "5", 0.8f, createdAt: DateTimeOffset.UnixEpoch), TitleExternalId.CreateNew(2, "mobygames", "9", 0.9f, createdAt: DateTimeOffset.UnixEpoch)],
            [
                TitleMetadataLayer.CreateNew(2, "screenscraper", MetadataSourceType.Provider,
                    new TitleMetadataPayload { Publisher = "Unlinked publisher" }, updatedAt: DateTimeOffset.UnixEpoch),
                TitleMetadataLayer.CreateNew(2, "mobygames", MetadataSourceType.Provider,
                    new TitleMetadataPayload { Developer = "Kept developer" }, updatedAt: DateTimeOffset.UnixEpoch)
            ],
            [
                TitleMedia.CreateNew(2, MediaType.Background, 4, "screenscraper", "image/png", createdAt: DateTimeOffset.UnixEpoch),
                TitleMedia.CreateNew(2, MediaType.Cover, 5, "mobygames", "image/png", createdAt: DateTimeOffset.UnixEpoch)
            ]);

        target.Absorb(source, ["igdb"], ["ScreenScraper"], new(DateTimeOffset.UnixEpoch,
            source.MetadataLayers.Select(layer => new KeyValuePair<string, DateTimeOffset>(layer.SourceId, DateTimeOffset.UnixEpoch))));

        target.Media.Select(media => media.SourceId).ShouldBe(["mobygames"]);
        target.ExternalIds.Select(link => link.Provider).ShouldBe(["mobygames"]);
        target.MetadataLayers.Select(layer => layer.SourceId).ShouldBe(["mobygames"]);
        target.Publisher.ShouldBeNull();
        target.Developer.ShouldBe("Kept developer");
        target.FieldProvenance.Values.ShouldNotContain("screenscraper");
    }

    private static Title Title(int id, IEnumerable<TitleExternalId>? externalIds = null,
        IEnumerable<TitleMetadataLayer>? metadataLayers = null, IEnumerable<TitleMedia>? media = null) =>
        Romd.Domain.Catalog.Title.Rehydrate(
            id: id,
            platformId: 10,
            name: "Super Mario World",
            normalizedName: "super mario world",
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.None,
            lastEnrichedAt: null,
            createdAt: DateTimeOffset.UtcNow,
            externalIds: externalIds,
            media: media,
            metadataLayers: metadataLayers);
}
