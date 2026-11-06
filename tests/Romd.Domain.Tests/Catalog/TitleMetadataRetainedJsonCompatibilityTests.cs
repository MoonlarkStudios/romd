using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class TitleMetadataRetainedJsonCompatibilityTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static TitleMetadataLayer Layer(string source, string json, DateTimeOffset? updatedAt = null) =>
        TitleMetadataLayer.Rehydrate(1, 1, source, MetadataSourceType.Provider, json, updatedAt ?? Timestamp);

    private static Title Create(params TitleMetadataLayer[] layers) => Title.Rehydrate(
        1, 10, "DAT Name", "dat name", null, null, null, null, null, null, null,
        EnrichmentStatus.None, null, Timestamp, metadataLayers: layers);

    private static string NullDescriptors(string code) => $$"""
        {"description":"Retained description","contentRatings":[{"board":{{(int)RatingBoard.Esrb}},"rawCode":"{{code}}","descriptors":null}]}
        """;

    private static string WithDescriptors(string code) => $$"""
        {"contentRatings":[{"board":{{(int)RatingBoard.Esrb}},"rawCode":"{{code}}","descriptors":["Existing descriptor"]}]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rematerialize_UnrecognizedClaimWithNullDescriptors_PreservesUnknownOrFallback(bool hasFallback)
    {
        var json = NullDescriptors("UNKNOWN CATEGORY");
        var unknown = Layer("primary", json);
        var title = hasFallback ? Create(unknown, Layer("fallback", WithDescriptors("T"))) : Create(unknown);

        title.Rematerialize(["primary", "fallback"]);

        title.Description.ShouldBe("Retained description");
        title.ContentRatingsMaterialized.ShouldBeTrue();
        if (hasFallback)
        {
            var rating = title.ContentRatings.ShouldHaveSingleItem();
            rating.Code.ShouldBe("T");
            rating.SourceId.ShouldBe("fallback");
            rating.Descriptors.ShouldBe(["Existing descriptor"]);
            title.ConservativeMinimumAge.ShouldBe(13);
        }
        else
        {
            title.ContentRatings.ShouldBeEmpty();
            title.ConservativeMinimumAge.ShouldBeNull();
        }
        unknown.MetadataJson.ShouldBe(json);
    }

    [Fact]
    public void Rematerialize_RecognizedClaimWithExplicitNullDescriptors_PreservesNullAndStoredJson()
    {
        var json = NullDescriptors("T");
        var layer = Layer("primary", json);
        var title = Create(layer);

        title.Rematerialize(["primary"]);

        var rating = title.ContentRatings.ShouldHaveSingleItem();
        rating.Code.ShouldBe("T");
        rating.SourceId.ShouldBe("primary");
        rating.Descriptors.ShouldBeNull();
        title.ConservativeMinimumAge.ShouldBe(13);
        layer.MetadataJson.ShouldBe(json);
    }

    [Fact]
    public void Rematerialize_ShadowedClaimWithNullDescriptors_PreservesWinningBoardAndOtherFields()
    {
        var json = NullDescriptors("M");
        var shadowed = Layer("secondary", json);
        var title = Create(Layer("primary", WithDescriptors("T")), shadowed);

        title.Rematerialize(["primary", "secondary"]);

        title.Description.ShouldBe("Retained description");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe("secondary");
        var rating = title.ContentRatings.ShouldHaveSingleItem();
        rating.Code.ShouldBe("T");
        rating.SourceId.ShouldBe("primary");
        rating.Descriptors.ShouldBe(["Existing descriptor"]);
        shadowed.MetadataJson.ShouldBe(json);
    }

    [Fact]
    public void Rematerialize_OverwrittenDuplicatePayloadContainsNullClaim_UsesLastOrderedPayloadWithoutVisitingUnusedClaims()
    {
        const string unusedJson = """{"description":"Unused","contentRatings":[null]}""";
        var unused = Layer("IGDB", unusedJson, Timestamp.AddDays(1));
        var title = Create(unused, Layer("igdb", WithDescriptors("T")));

        title.Rematerialize(["igdb"]);

        title.Description.ShouldBeNull();
        title.FieldProvenance.ShouldBeEmpty();
        var rating = title.ContentRatings.ShouldHaveSingleItem();
        rating.Code.ShouldBe("T");
        rating.SourceId.ShouldBe("IGDB");
        rating.Descriptors.ShouldBe(["Existing descriptor"]);
        title.ConservativeMinimumAge.ShouldBe(13);
        unused.MetadataJson.ShouldBe(unusedJson);
    }
}
