using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleRematerializeContentRatingsTests
{
    #region Helpers

    private static Title CreateTitle(
        IEnumerable<TitleMetadataLayer>? layers = null,
        Dictionary<string, string>? fieldSourceOverrides = null)
    {
        return Title.Rehydrate(
            id: 1,
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
            metadataLayers: layers,
            fieldSourceOverrides: fieldSourceOverrides);
    }

    private static TitleMetadataLayer CreateLayer(
        string sourceId,
        MetadataSourceType sourceType,
        TitleMetadataPayload payload)
    {
        return TitleMetadataLayer.CreateNew(1, sourceId, sourceType, payload);
    }

    private static ContentRatingClaim Claim(RatingBoard board, string rawCode) =>
        new() { Board = board, RawCode = rawCode };

    private static ContentRating RatingFor(Title title, RatingBoard board) =>
        title.ContentRatings.Single(r => r.Board == board);

    #endregion

    [Fact]
    public void Rematerialize_NoClaims_NoRatingsAndNullConservativeAge()
    {
        var title = CreateTitle();

        title.Rematerialize(["igdb"]);

        title.ContentRatings.ShouldBeEmpty();
        title.ConservativeMinimumAge.ShouldBeNull();
        title.ContentRatingsMaterialized.ShouldBeTrue();
    }

    [Fact]
    public void Rematerialize_SingleProviderClaims_MaterializesOneRatingPerBoard()
    {
        // Claims survive the JSON round-trip through the layer, then resolve per board
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings =
            [
                Claim(RatingBoard.Esrb, "E10"),
                Claim(RatingBoard.Pegi, "Twelve"),
                Claim(RatingBoard.Cero, "CERO_B")
            ]
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);

        title.ContentRatings.Count.ShouldBe(3);
        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("E10+");
        RatingFor(title, RatingBoard.Pegi).Code.ShouldBe("PEGI 12");
        RatingFor(title, RatingBoard.Cero).Code.ShouldBe("CERO B");
        title.ContentRatings.ShouldAllBe(r => r.SourceId == "igdb");
    }

    [Fact]
    public void Rematerialize_TwoProviders_PerBoardPriorityWithGapFilling()
    {
        // igdb (higher priority) wins ESRB; screenscraper fills the board igdb lacks
        var igdb = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "T")]
        });
        var screenscraper = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M"), Claim(RatingBoard.Pegi, "16")]
        });

        var title = CreateTitle(layers: [igdb, screenscraper]);

        title.Rematerialize(["igdb", "screenscraper"]);

        title.ContentRatings.Count.ShouldBe(2);
        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("T");
        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("igdb");
        RatingFor(title, RatingBoard.Pegi).Code.ShouldBe("PEGI 16");
        RatingFor(title, RatingBoard.Pegi).SourceId.ShouldBe("screenscraper");
    }

    [Fact]
    public void Rematerialize_UserClaim_WinsForItsBoardOnly()
    {
        var user = CreateLayer("user", MetadataSourceType.User, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "E")]
        });
        var igdb = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M"), Claim(RatingBoard.Pegi, "18")]
        });

        var title = CreateTitle(layers: [user, igdb]);

        title.Rematerialize(["igdb"]);

        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("E");
        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("user");
        RatingFor(title, RatingBoard.Pegi).Code.ShouldBe("PEGI 18");
        RatingFor(title, RatingBoard.Pegi).SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void Rematerialize_FieldSourceOverride_TakesPrecedenceOverUser()
    {
        var user = CreateLayer("user", MetadataSourceType.User, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "E")]
        });
        var igdb = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        });

        var title = CreateTitle(
            layers: [user, igdb],
            fieldSourceOverrides: new Dictionary<string, string>
            {
                [Title.ContentRatingsFieldName] = "igdb"
            });

        title.Rematerialize(["igdb"]);

        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("M");
        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void Rematerialize_UnrecognizedClaim_DroppedAndLowerPriorityFillsBoard()
    {
        var igdb = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Pegi, "NOT A RATING")]
        });
        var screenscraper = CreateLayer("screenscraper", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Pegi, "PEGI 7")]
        });

        var title = CreateTitle(layers: [igdb, screenscraper]);

        title.Rematerialize(["igdb", "screenscraper"]);

        title.ContentRatings.Count.ShouldBe(1);
        RatingFor(title, RatingBoard.Pegi).Code.ShouldBe("PEGI 7");
        RatingFor(title, RatingBoard.Pegi).SourceId.ShouldBe("screenscraper");
    }

    [Fact]
    public void Rematerialize_ConservativeMinimumAge_IsMaxAcrossRatedBoards()
    {
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings =
            [
                Claim(RatingBoard.Esrb, "E10"),   // 10
                Claim(RatingBoard.Pegi, "16"),    // 16
                Claim(RatingBoard.Cero, "B")      // 12
            ]
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);

        title.ConservativeMinimumAge.ShouldBe(16);
    }

    [Fact]
    public void Rematerialize_PendingAndRefused_ExcludedFromConservativeAge()
    {
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings =
            [
                Claim(RatingBoard.Esrb, "RP"),
                Claim(RatingBoard.Acb, "RC"),
                Claim(RatingBoard.Usk, "6")
            ]
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);

        title.ContentRatings.Count.ShouldBe(3);
        title.ConservativeMinimumAge.ShouldBe(6);
    }

    [Fact]
    public void Rematerialize_OnlyNonRatedDesignations_NullConservativeAge()
    {
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "RP")]
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);

        title.ConservativeMinimumAge.ShouldBeNull();
        RatingFor(title, RatingBoard.Esrb).Designation.ShouldBe(RatingDesignation.RatingPending);
    }

    [Fact]
    public void Rematerialize_RunTwice_IsIdempotent()
    {
        var layer = CreateLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
        {
            ContentRatings = [Claim(RatingBoard.Esrb, "M")]
        });

        var title = CreateTitle(layers: [layer]);

        title.Rematerialize(["igdb"]);
        title.Rematerialize(["igdb"]);

        title.ContentRatings.Count.ShouldBe(1);
        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("M");
        title.ConservativeMinimumAge.ShouldBe(17);
    }
}
