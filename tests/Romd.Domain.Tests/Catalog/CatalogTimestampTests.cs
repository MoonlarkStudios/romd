using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class CatalogTimestampTests
{
    private static readonly DateTimeOffset Created = new DateTimeOffset(2026, 11, 4, 12, 13, 14, TimeSpan.FromHours(-3)).AddTicks(1234567);
    private static readonly DateTimeOffset Updated = Created.AddHours(2).AddTicks(1);

    [Fact]
    public void CreateNew_AllCatalogEntities_KeepSuppliedTimePrecisionAndOffset()
    {
        var title = Title.CreateNew(1, "Title", "title", Created);
        var bios = Bios.CreateNew(1, "BIOS", "bios", Created);
        var link = TitleExternalId.CreateNew(1, "provider", "42", 1, Created);
        var media = TitleMedia.CreateNew(1, MediaType.Cover, 1, "provider", "image/png", Created);
        var layer = TitleMetadataLayer.CreateNew(1, "provider", MetadataSourceType.Provider, new(), Created);

        foreach (var actual in new[] { title.CreatedAt, bios.CreatedAt, link.CreatedAt, media.CreatedAt, layer.UpdatedAt })
        {
            actual.Ticks.ShouldBe(Created.Ticks);
            actual.Offset.ShouldBe(Created.Offset);
        }
        title.Revision.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData(EnrichmentStatus.Completed)]
    [InlineData(EnrichmentStatus.Failed)]
    [InlineData(EnrichmentStatus.NotFound)]
    [InlineData(EnrichmentStatus.LowConfidence)]
    public void MarkEnrichment_ExplicitCompletionTime_PreservesOtherStateAndRevision(EnrichmentStatus status)
    {
        var title = Title.CreateNew(1, "Title", "title", Created);
        var revision = title.Revision;

        switch (status)
        {
            case EnrichmentStatus.Completed:
                title.MarkEnrichmentCompleted(Updated);
                break;
            case EnrichmentStatus.Failed:
                title.MarkEnrichmentFailed(Updated);
                break;
            case EnrichmentStatus.NotFound:
                title.MarkEnrichmentNotFound(Updated);
                break;
            case EnrichmentStatus.LowConfidence:
                title.MarkEnrichmentLowConfidence(Updated);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        title.LastEnrichedAt.ShouldBe(Updated);
        var actual = title.LastEnrichedAt.ShouldNotBeNull();
        actual.Ticks.ShouldBe(Updated.Ticks);
        actual.Offset.ShouldBe(Updated.Offset);
        title.EnrichmentStatus.ShouldBe(status);
        title.CreatedAt.ShouldBe(Created);
        title.Revision.ShouldBe(revision);
    }

    [Fact]
    public void StoreLayerAndUserRatings_ExplicitTimes_PreservesExistingRewriteAndEmptyClaimBehavior()
    {
        var title = Title.CreateNew(1, "Title", "title", Created);
        title.StoreProviderLayer("provider", MetadataSourceType.Provider, new() { Description = "First" }, Created);
        var provider = title.GetMetadataLayer("provider").ShouldNotBeNull();

        title.StoreProviderLayer("provider", MetadataSourceType.Provider, new() { Description = "Updated" }, Updated);
        title.StoreUserLayer(new() { Publisher = "User" }, Created);
        title.SetUserContentRating(new() { Board = RatingBoard.Esrb, RawCode = "T" }, Updated);

        title.GetMetadataLayer("provider").ShouldBeSameAs(provider);
        provider.UpdatedAt.Ticks.ShouldBe(Updated.Ticks);
        var user = title.GetMetadataLayer("user").ShouldNotBeNull();
        user.UpdatedAt.ShouldBe(Updated);
        title.ClearUserContentRating(RatingBoard.Pegi, Updated.AddDays(1));
        user.UpdatedAt.ShouldBe(Updated.AddDays(1));
        title.ClearUserContentRating(RatingBoard.Esrb, Updated.AddDays(2));
        user.UpdatedAt.ShouldBe(Updated.AddDays(2));
        title.ClearUserContentRating(RatingBoard.Pegi, Updated.AddDays(3));
        user.UpdatedAt.ShouldBe(Updated.AddDays(2));
        user.GetPayload().ShouldNotBeNull().Publisher.ShouldBe("User");
        provider.GetPayload().ShouldNotBeNull().Description.ShouldBe("Updated");
    }

    [Fact]
    public void StoreProviderLayer_InvalidSource_RejectsWithoutChangingExistingLayerOrTimestamp()
    {
        var title = Title.CreateNew(1, "Title", "title", Created);
        title.StoreProviderLayer("provider", MetadataSourceType.Provider, new() { Description = "Existing" }, Created);

        Should.Throw<ArgumentException>(() => title.StoreProviderLayer(" ", MetadataSourceType.Provider, new(), Updated));

        var layer = title.MetadataLayers.ShouldHaveSingleItem();
        layer.SourceId.ShouldBe("provider");
        layer.UpdatedAt.ShouldBe(Created);
        layer.GetPayload().ShouldNotBeNull().Description.ShouldBe("Existing");
    }
}
