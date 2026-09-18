using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleConfirmExternalIdTests
{
    private static Title CreateTitleWithExternalId(
        string provider = "igdb",
        string externalId = "12345",
        float confidence = 0.7f,
        EnrichmentStatus status = EnrichmentStatus.LowConfidence)
    {
        var ext = TitleExternalId.Rehydrate(
            id: 1,
            titleId: 1,
            provider: provider,
            externalId: externalId,
            matchConfidence: confidence,
            isConfirmed: false,
            createdAt: DateTimeOffset.UtcNow);

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
            enrichmentStatus: status,
            lastEnrichedAt: DateTimeOffset.UtcNow,
            createdAt: DateTimeOffset.UtcNow,
            externalIds: [ext]);
    }

    [Fact]
    public void ConfirmExternalId_WhenExists_ConfirmsAndReturnsTrue()
    {
        var title = CreateTitleWithExternalId();

        var result = title.ConfirmExternalId("igdb");

        result.ShouldBeTrue();
        title.GetExternalId("igdb")!.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void ConfirmExternalId_WhenLowConfidence_TransitionsToCompleted()
    {
        var title = CreateTitleWithExternalId(status: EnrichmentStatus.LowConfidence);

        title.ConfirmExternalId("igdb");

        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Completed);
    }

    [Fact]
    public void ConfirmExternalId_WhenCompleted_StaysCompleted()
    {
        var title = CreateTitleWithExternalId(status: EnrichmentStatus.Completed);

        title.ConfirmExternalId("igdb");

        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Completed);
    }

    [Fact]
    public void ConfirmExternalId_WhenNoExternalId_ReturnsFalse()
    {
        var title = Title.Rehydrate(
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
            createdAt: DateTimeOffset.UtcNow);

        var result = title.ConfirmExternalId("igdb");

        result.ShouldBeFalse();
    }

    [Fact]
    public void ConfirmExternalId_IsIdempotent()
    {
        var title = CreateTitleWithExternalId(status: EnrichmentStatus.LowConfidence);

        title.ConfirmExternalId("igdb");
        title.ConfirmExternalId("igdb");

        title.GetExternalId("igdb")!.IsConfirmed.ShouldBeTrue();
        title.EnrichmentStatus.ShouldBe(EnrichmentStatus.Completed);
    }

    [Fact]
    public void SetExternalIdFromAutoEnrichment_CreatesUnconfirmed()
    {
        var title = Title.Rehydrate(
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
            createdAt: DateTimeOffset.UtcNow);

        title.SetExternalIdFromAutoEnrichment("igdb", "12345", 0.85f);

        var ext = title.GetExternalId("igdb");
        ext.ShouldNotBeNull();
        ext.IsConfirmed.ShouldBeFalse();
        ext.MatchConfidence.ShouldBe(0.85f);
    }

    [Fact]
    public void SetExternalIdFromAutoEnrichment_WhenConfirmed_ReturnsFalse()
    {
        var title = CreateTitleWithExternalId();
        title.ConfirmExternalId("igdb");

        var result = title.SetExternalIdFromAutoEnrichment("igdb", "99999", 0.95f);

        result.ShouldBeFalse();
        title.GetExternalId("igdb")!.ExternalId.ShouldBe("12345");
    }

    [Fact]
    public void SetExternalIdManually_CreatesConfirmed()
    {
        var title = Title.Rehydrate(
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
            createdAt: DateTimeOffset.UtcNow);

        title.SetExternalIdManually("igdb", "12345");

        var ext = title.GetExternalId("igdb");
        ext.ShouldNotBeNull();
        ext.IsConfirmed.ShouldBeTrue();
        ext.MatchConfidence.ShouldBe(1.0f);
    }

    [Fact]
    public void SetExternalIdManually_OverwritesExistingConfirmed()
    {
        var title = CreateTitleWithExternalId();
        title.ConfirmExternalId("igdb");

        title.SetExternalIdManually("igdb", "99999");

        var ext = title.GetExternalId("igdb");
        ext!.ExternalId.ShouldBe("99999");
        ext.IsConfirmed.ShouldBeTrue();
        ext.MatchConfidence.ShouldBe(1.0f);
    }
}
