using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public class MetadataMergerTests
{
    private readonly EnrichmentOptions _options = new() { GlobalSourcePriority = ["igdb", "screenscraper"] };

    private readonly IPlatformFieldDefaultRepository _platformFieldDefaultRepo =
        Substitute.For<IPlatformFieldDefaultRepository>();

    private MetadataMerger CreateMerger() => new(_platformFieldDefaultRepo, Options.Create(_options));

    private static Title CreateTitle(
        IEnumerable<TitleMetadataLayer>? layers = null,
        IEnumerable<TitleExternalId>? externalIds = null)
    {
        return Title.Rehydrate(
            1, 10, "Test Title",
            "test title",
            null, null, null, null,
            null, null, null,
            EnrichmentStatus.None, null,
            DateTimeOffset.UtcNow,
            metadataLayers: layers,
            externalIds: externalIds);
    }

    [Fact]
    public void StoreProviderResult_StoresLayerNotOldUpdateProviderMetadata()
    {
        var title = CreateTitle();
        var merger = CreateMerger();

        var result = EnrichmentResult.Found("12345", 0.9f,
            new EnrichmentData { Description = "A great game", Publisher = "Nintendo" });

        merger.StoreProviderResult(title, "igdb", result);

        // Layer is stored
        title.MetadataLayers.Count.ShouldBe(1);
        var layer = title.MetadataLayers.First();
        layer.SourceId.ShouldBe("igdb");
        layer.SourceType.ShouldBe(MetadataSourceType.Provider);

        var payload = layer.GetPayload();
        payload.ShouldNotBeNull();
        payload.Description.ShouldBe("A great game");
        payload.Publisher.ShouldBe("Nintendo");

        // StoreProviderLayer does NOT rematerialize — effective fields should still be null
        title.Description.ShouldBeNull();
        title.Publisher.ShouldBeNull();
        title.FieldProvenance.ShouldBeEmpty();
    }

    [Fact]
    public void StoreProviderResult_SetsExternalId()
    {
        var title = CreateTitle();
        var merger = CreateMerger();

        var result = EnrichmentResult.Found("99999", 0.85f, new EnrichmentData { Genre = "RPG" });

        merger.StoreProviderResult(title, "igdb", result);

        title.ExternalIds.Count.ShouldBe(1);
        var extId = title.GetExternalId("igdb");
        extId.ShouldNotBeNull();
        extId.ExternalId.ShouldBe("99999");
        extId.MatchConfidence.ShouldBe(0.85f);
    }

    [Fact]
    public void StoreProviderResult_UpdatesExistingExternalId()
    {
        var existingExtId = TitleExternalId.CreateNew(1, "igdb", "11111", 0.6f);
        var title = CreateTitle(externalIds: [existingExtId]);
        var merger = CreateMerger();

        var result = EnrichmentResult.Found("22222", 0.95f, new EnrichmentData { Description = "Updated" });

        merger.StoreProviderResult(title, "igdb", result);

        // Should update existing, not create duplicate
        title.ExternalIds.Count.ShouldBe(1);
        var extId = title.GetExternalId("igdb");
        extId.ShouldNotBeNull();
        extId.ExternalId.ShouldBe("22222");
        extId.MatchConfidence.ShouldBe(0.95f);
    }

    [Fact]
    public void StoreProviderResult_NoExternalId_DoesNotSetOne()
    {
        var title = CreateTitle();
        var merger = CreateMerger();

        var result = new EnrichmentResult
        {
            Outcome = EnrichmentOutcome.Found,
            ExternalId = null,
            MatchConfidence = 0.7f,
            Data = new EnrichmentData { Description = "Found via hash" }
        };

        merger.StoreProviderResult(title, "screenscraper", result);

        title.ExternalIds.ShouldBeEmpty();
        title.MetadataLayers.Count.ShouldBe(1);
    }

    [Fact]
    public void StoreProviderResult_NoData_DoesNotCreateLayer()
    {
        var title = CreateTitle();
        var merger = CreateMerger();

        var result = EnrichmentResult.Found("12345", 0.9f, new EnrichmentData());

        merger.StoreProviderResult(title, "igdb", result);

        // External ID is set even without data
        title.ExternalIds.Count.ShouldBe(1);
        // But no layer is created since data has no values
        title.MetadataLayers.ShouldBeEmpty();
    }

    [Fact]
    public void StoreProviderResult_ContentRatingClaimsOnly_CreatesLayer()
    {
        var title = CreateTitle();
        var merger = CreateMerger();

        var claims = new[]
        {
            new ContentRatingClaim
            {
                Board = RatingBoard.Esrb,
                RawCode = "T",
                ExternalRatingId = "1001",
                Descriptors = ["Fantasy Violence"],
                Synopsis = "ESRB synopsis"
            }
        };
        var result = EnrichmentResult.Found("12345", 0.9f, new EnrichmentData { ContentRatings = claims });

        merger.StoreProviderResult(title, "igdb", result);

        title.MetadataLayers.Count.ShouldBe(1);
        var payload = title.MetadataLayers.Single().GetPayload();
        payload.ShouldNotBeNull();
        payload.ContentRatings.ShouldNotBeNull();
        payload.ContentRatings.Count.ShouldBe(1);
        payload.ContentRatings[0].Board.ShouldBe(RatingBoard.Esrb);
        payload.ContentRatings[0].RawCode.ShouldBe("T");
        payload.ContentRatings[0].Descriptors.ShouldBe(["Fantasy Violence"]);
        payload.ContentRatings[0].Synopsis.ShouldBe("ESRB synopsis");
    }

    [Fact]
    public void StoreProviderResult_NullData_DoesNotCreateLayer()
    {
        var title = CreateTitle();
        var merger = CreateMerger();

        var result = new EnrichmentResult
        {
            Outcome = EnrichmentOutcome.Found, ExternalId = "12345", MatchConfidence = 0.9f, Data = null
        };

        merger.StoreProviderResult(title, "igdb", result);

        title.ExternalIds.Count.ShouldBe(1);
        title.MetadataLayers.ShouldBeEmpty();
    }

    [Fact]
    public async Task RematerializeAsync_LoadsPlatformDefaults()
    {
        var igdbLayer = TitleMetadataLayer.CreateNew(1, "igdb", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "IGDB desc", Genre = "Platformer" });
        var ssLayer = TitleMetadataLayer.CreateNew(1, "screenscraper", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "SS desc", Genre = "Action" });

        var title = CreateTitle([igdbLayer, ssLayer]);

        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Genre"] = "screenscraper" });

        var merger = CreateMerger();
        await merger.RematerializeAsync(title);

        // Genre from screenscraper via platform default
        title.Genre.ShouldBe("Action");
        title.FieldProvenance["Genre"].ShouldBe("screenscraper");

        // Description from igdb via global priority
        title.Description.ShouldBe("IGDB desc");
        title.FieldProvenance["Description"].ShouldBe("igdb");
    }

    [Fact]
    public async Task RematerializeAsync_NoPlatformDefaults_UsesGlobalPriority()
    {
        var igdbLayer = TitleMetadataLayer.CreateNew(1, "igdb", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "IGDB desc" });
        var ssLayer = TitleMetadataLayer.CreateNew(1, "screenscraper", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "SS desc" });

        var title = CreateTitle([igdbLayer, ssLayer]);

        _platformFieldDefaultRepo.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var merger = CreateMerger();
        await merger.RematerializeAsync(title);

        title.Description.ShouldBe("IGDB desc");
        title.FieldProvenance["Description"].ShouldBe("igdb");
    }

    [Fact]
    public void StoreProviderResult_MultipleProviders_IndependentLayers()
    {
        var title = CreateTitle();
        var merger = CreateMerger();

        var resultA = EnrichmentResult.Found("aaa", 0.9f,
            new EnrichmentData { Description = "IGDB desc", Publisher = "IGDB pub" });

        var resultB =
            EnrichmentResult.Found("bbb", 0.8f, new EnrichmentData { Developer = "SS dev", Genre = "Action" });

        merger.StoreProviderResult(title, "igdb", resultA);
        merger.StoreProviderResult(title, "screenscraper", resultB);

        // Two separate layers
        title.MetadataLayers.Count.ShouldBe(2);
        title.MetadataLayers.ShouldContain(l => l.SourceId == "igdb");
        title.MetadataLayers.ShouldContain(l => l.SourceId == "screenscraper");

        // Two separate external IDs
        title.ExternalIds.Count.ShouldBe(2);

        // No rematerialization happened — fields still null
        title.Description.ShouldBeNull();
        title.Developer.ShouldBeNull();
    }

    [Fact]
    public void StoreProviderResult_ConfirmedExternalId_NotOverwritten()
    {
        var confirmedExt = TitleExternalId.Rehydrate(1, 1, "igdb", "confirmed_id", 1.0f, true, DateTimeOffset.UtcNow);
        var title = CreateTitle(externalIds: [confirmedExt]);
        var merger = CreateMerger();

        var result = EnrichmentResult.Found("new_id", 0.95f, new EnrichmentData { Description = "Fresh metadata" });

        merger.StoreProviderResult(title, "igdb", result);

        // External ID should remain unchanged (confirmed)
        title.ExternalIds.Count.ShouldBe(1);
        var extId = title.GetExternalId("igdb");
        extId.ShouldNotBeNull();
        extId.ExternalId.ShouldBe("confirmed_id");
        extId.IsConfirmed.ShouldBeTrue();

        // A rejected identity cannot supply content under the confirmed identity's name.
        title.MetadataLayers.ShouldBeEmpty();
    }
}
