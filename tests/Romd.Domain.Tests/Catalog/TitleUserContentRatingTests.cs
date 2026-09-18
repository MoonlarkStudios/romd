using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleUserContentRatingTests
{
    private static Title CreateTitle(IEnumerable<TitleMetadataLayer>? layers = null) =>
        Title.Rehydrate(
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
            metadataLayers: layers);

    private static TitleMetadataLayer ProviderLayer(string sourceId, TitleMetadataPayload payload) =>
        TitleMetadataLayer.CreateNew(1, sourceId, MetadataSourceType.Provider, payload);

    private static ContentRating RatingFor(Title title, RatingBoard board) =>
        title.ContentRatings.Single(r => r.Board == board);

    [Fact]
    public void SetUserContentRating_WinsCascadeForItsBoard_LeavesUserProvenance()
    {
        var igdb = ProviderLayer("igdb", new TitleMetadataPayload
        {
            ContentRatings = [new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "M" }]
        });
        var title = CreateTitle([igdb]);

        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E" });
        title.Rematerialize(["igdb"]);

        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("E");
        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("user");
        title.ConservativeMinimumAge.ShouldBe(0);
    }

    [Fact]
    public void SetUserContentRating_DoesNotDisturbOtherBoards()
    {
        var igdb = ProviderLayer("igdb", new TitleMetadataPayload
        {
            ContentRatings =
            [
                new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "M" },
                new ContentRatingClaim { Board = RatingBoard.Pegi, RawCode = "18" }
            ]
        });
        var title = CreateTitle([igdb]);

        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E" });
        title.Rematerialize(["igdb"]);

        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("user");
        RatingFor(title, RatingBoard.Pegi).Code.ShouldBe("PEGI 18");
        RatingFor(title, RatingBoard.Pegi).SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void SetUserContentRating_ReplacesExistingUserClaimForSameBoard()
    {
        var title = CreateTitle();

        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "T" });
        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E10+" });
        title.Rematerialize(["igdb"]);

        title.ContentRatings.Count(r => r.Board == RatingBoard.Esrb).ShouldBe(1);
        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("E10+");
    }

    [Fact]
    public void ClearUserContentRating_RevertsBoardToProviderCascade()
    {
        var igdb = ProviderLayer("igdb", new TitleMetadataPayload
        {
            ContentRatings = [new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "M" }]
        });
        var title = CreateTitle([igdb]);

        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E" });
        title.ClearUserContentRating(RatingBoard.Esrb);
        title.Rematerialize(["igdb"]);

        RatingFor(title, RatingBoard.Esrb).Code.ShouldBe("M");
        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void ClearUserContentRating_PreservesUserClaimsForOtherBoards()
    {
        var title = CreateTitle();

        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E" });
        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Pegi, RawCode = "3" });
        title.ClearUserContentRating(RatingBoard.Esrb);
        title.Rematerialize(["igdb"]);

        title.ContentRatings.ShouldHaveSingleItem();
        RatingFor(title, RatingBoard.Pegi).Code.ShouldBe("PEGI 3");
        RatingFor(title, RatingBoard.Pegi).SourceId.ShouldBe("user");
    }

    [Fact]
    public void ClearUserContentRating_WhenNoUserClaim_IsNoOp()
    {
        var igdb = ProviderLayer("igdb", new TitleMetadataPayload
        {
            ContentRatings = [new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "M" }]
        });
        var title = CreateTitle([igdb]);

        Should.NotThrow(() => title.ClearUserContentRating(RatingBoard.Pegi));
        title.Rematerialize(["igdb"]);

        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void SetUserContentRating_PreservesExistingUserScalarOverrides()
    {
        var title = CreateTitle();
        title.StoreUserLayer(new TitleMetadataPayload { Genre = "Platform" });

        title.SetUserContentRating(new ContentRatingClaim { Board = RatingBoard.Esrb, RawCode = "E" });
        title.Rematerialize(["igdb"]);

        title.Genre.ShouldBe("Platform");
        RatingFor(title, RatingBoard.Esrb).SourceId.ShouldBe("user");
    }
}
