using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles.Enrichment;

public sealed class ProviderMetadataLayersTests
{
    private static Title CreateTitle(IEnumerable<TitleExternalId>? externalIds = null) => Title.Rehydrate(
        1, 10, "Test Title", "test title", null, null, null, null, null, null, null,
        EnrichmentStatus.None, null, DateTimeOffset.UtcNow, externalIds: externalIds);

    [Fact]
    public void Store_ProviderData_StoresLayerWithoutRematerializing()
    {
        var title = CreateTitle();

        ProviderMetadataLayers.Store(title, "igdb", EnrichmentResult.Found("12345", 0.9f,
            new EnrichmentData { Description = "A great game", Publisher = "Nintendo" }), updatedAt: DateTimeOffset.UnixEpoch);

        var layer = title.MetadataLayers.ShouldHaveSingleItem();
        layer.SourceId.ShouldBe("igdb");
        layer.SourceType.ShouldBe(MetadataSourceType.Provider);
        layer.UpdatedAt.ShouldBe(DateTimeOffset.UnixEpoch);
        var payload = layer.GetPayload().ShouldNotBeNull();
        payload.Description.ShouldBe("A great game");
        payload.Publisher.ShouldBe("Nintendo");
        payload.ProviderGameId.ShouldBe("12345");
        title.Description.ShouldBeNull();
        title.FieldProvenance.ShouldBeEmpty();
    }

    [Fact]
    public void Store_AcceptedResult_NeverWritesTheProviderAssociation()
    {
        var confirmed = TitleExternalId.Rehydrate(1, 1, "igdb", "confirmed", 1.0f, true, DateTimeOffset.UtcNow);
        var title = CreateTitle([confirmed]);

        ProviderMetadataLayers.Store(title, "igdb", EnrichmentResult.Found("confirmed", 0.2f,
            new EnrichmentData { Genre = "RPG" }), updatedAt: DateTimeOffset.UnixEpoch);

        var link = title.ExternalIds.ShouldHaveSingleItem();
        link.ExternalId.ShouldBe("confirmed");
        link.MatchConfidence.ShouldBe(1.0f);
        link.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void Store_ContentRatingClaimsOnly_CreatesLayer()
    {
        var title = CreateTitle();
        var claim = new ContentRatingClaim
        {
            Board = RatingBoard.Esrb,
            RawCode = "T",
            ExternalRatingId = "1001",
            Descriptors = ["Fantasy Violence"],
            Synopsis = "ESRB synopsis"
        };

        ProviderMetadataLayers.Store(title, "igdb",
            EnrichmentResult.Found("12345", 0.9f, new EnrichmentData { ContentRatings = [claim] }), updatedAt: DateTimeOffset.UnixEpoch);

        var stored = title.MetadataLayers.ShouldHaveSingleItem().GetPayload()!.ContentRatings.ShouldNotBeNull()
            .ShouldHaveSingleItem();
        stored.Board.ShouldBe(RatingBoard.Esrb);
        stored.RawCode.ShouldBe("T");
        stored.Descriptors.ShouldBe(["Fantasy Violence"]);
        stored.Synopsis.ShouldBe("ESRB synopsis");
    }

    [Fact]
    public void Store_EmptyData_DoesNotCreateLayer()
    {
        var title = CreateTitle();

        ProviderMetadataLayers.Store(title, "igdb", EnrichmentResult.Found("12345", 0.9f, new EnrichmentData()), updatedAt: DateTimeOffset.UnixEpoch);

        title.MetadataLayers.ShouldBeEmpty();
        title.ExternalIds.ShouldBeEmpty();
    }

    [Fact]
    public void Store_NullData_DoesNotCreateLayer()
    {
        var title = CreateTitle();

        ProviderMetadataLayers.Store(title, "igdb",
            new EnrichmentResult { Outcome = EnrichmentOutcome.Found, ExternalId = "12345", Data = null }, updatedAt: DateTimeOffset.UnixEpoch);

        title.MetadataLayers.ShouldBeEmpty();
    }

    [Fact]
    public void Store_MultipleProviders_KeepsIndependentLayers()
    {
        var title = CreateTitle();

        ProviderMetadataLayers.Store(title, "igdb",
            EnrichmentResult.Found("aaa", 0.9f, new EnrichmentData { Description = "IGDB desc" }), updatedAt: DateTimeOffset.UnixEpoch);
        ProviderMetadataLayers.Store(title, "screenscraper",
            EnrichmentResult.Found("bbb", 0.8f, new EnrichmentData { Developer = "SS dev" }), updatedAt: DateTimeOffset.UnixEpoch);

        title.MetadataLayers.Select(l => l.SourceId).Order().ShouldBe(["igdb", "screenscraper"]);
        title.Description.ShouldBeNull();
        title.Developer.ShouldBeNull();
    }

    [Fact]
    public void Store_SameProviderAgain_ReplacesItsLayer()
    {
        var title = CreateTitle();

        ProviderMetadataLayers.Store(title, "igdb",
            EnrichmentResult.Found("aaa", 0.9f, new EnrichmentData { Description = "Old" }), updatedAt: DateTimeOffset.UnixEpoch);
        ProviderMetadataLayers.Store(title, "igdb",
            EnrichmentResult.Found("aaa", 0.9f, new EnrichmentData { Description = "New" }), updatedAt: DateTimeOffset.UnixEpoch);

        title.MetadataLayers.ShouldHaveSingleItem().GetPayload()!.Description.ShouldBe("New");
    }
}
