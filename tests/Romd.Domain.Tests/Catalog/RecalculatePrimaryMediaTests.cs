using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class RecalculatePrimaryMediaTests
{
    private static Title CreateTitle(IEnumerable<TitleMedia>? media = null)
    {
        return Title.Rehydrate(
            id: 1, platformId: 10, name: "Test Title",
            normalizedName: "test title",
            description: null, publisher: null, developer: null, genre: null,
            releaseDate: null, players: null, rating: null,
            enrichmentStatus: EnrichmentStatus.None, lastEnrichedAt: null,
            createdAt: DateTimeOffset.UtcNow,
            media: media);
    }

    private static TitleMedia CreateMedia(
        int id, MediaType type, string sourceId,
        bool isPrimary = false, DateTimeOffset? createdAt = null)
    {
        return TitleMedia.Rehydrate(
            id: id, titleId: 1, type: type, fileId: id * 100,
            sourceId: sourceId, contentType: "image/jpeg",
            isPrimary: isPrimary, sourceUrl: null,
            createdAt: createdAt ?? DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RecalculatePrimaryMedia_UserMediaAlwaysWins()
    {
        var igdbCover = CreateMedia(1, MediaType.Cover, "igdb",
            createdAt: DateTimeOffset.UtcNow.AddDays(-10));
        var userCover = CreateMedia(2, MediaType.Cover, "user",
            createdAt: DateTimeOffset.UtcNow);

        var title = CreateTitle(media: [igdbCover, userCover]);

        title.RecalculatePrimaryMedia(["igdb", "screenscraper"]);

        title.GetPrimaryMedia(MediaType.Cover).ShouldNotBeNull();
        title.GetPrimaryMedia(MediaType.Cover)!.SourceId.ShouldBe("user");
        title.GetPrimaryMedia(MediaType.Cover)!.Id.ShouldBe(2);
    }

    [Fact]
    public void RecalculatePrimaryMedia_ProviderPriorityRespected()
    {
        var ssCover = CreateMedia(1, MediaType.Cover, "screenscraper",
            createdAt: DateTimeOffset.UtcNow.AddDays(-5));
        var igdbCover = CreateMedia(2, MediaType.Cover, "igdb",
            createdAt: DateTimeOffset.UtcNow);

        var title = CreateTitle(media: [ssCover, igdbCover]);

        // igdb is first in priority, so igdb cover should be primary
        title.RecalculatePrimaryMedia(["igdb", "screenscraper"]);

        var primary = title.GetPrimaryMedia(MediaType.Cover);
        primary.ShouldNotBeNull();
        primary.SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void RecalculatePrimaryMedia_ReversedPriority_SecondProviderWins()
    {
        var ssCover = CreateMedia(1, MediaType.Cover, "screenscraper",
            createdAt: DateTimeOffset.UtcNow.AddDays(-5));
        var igdbCover = CreateMedia(2, MediaType.Cover, "igdb",
            createdAt: DateTimeOffset.UtcNow);

        var title = CreateTitle(media: [ssCover, igdbCover]);

        // screenscraper is first in priority now
        title.RecalculatePrimaryMedia(["screenscraper", "igdb"]);

        var primary = title.GetPrimaryMedia(MediaType.Cover);
        primary.ShouldNotBeNull();
        primary.SourceId.ShouldBe("screenscraper");
    }

    [Fact]
    public void RecalculatePrimaryMedia_MultipleTypes_IndependentResolution()
    {
        var igdbCover = CreateMedia(1, MediaType.Cover, "igdb");
        var ssCover = CreateMedia(2, MediaType.Cover, "screenscraper");
        var ssScreenshot = CreateMedia(3, MediaType.Screenshot, "screenscraper");
        var igdbScreenshot = CreateMedia(4, MediaType.Screenshot, "igdb");

        var title = CreateTitle(media: [igdbCover, ssCover, ssScreenshot, igdbScreenshot]);

        title.RecalculatePrimaryMedia(["igdb", "screenscraper"]);

        // Cover: igdb wins (higher priority)
        var primaryCover = title.GetPrimaryMedia(MediaType.Cover);
        primaryCover.ShouldNotBeNull();
        primaryCover.SourceId.ShouldBe("igdb");

        // Screenshot: igdb also wins (higher priority)
        var primaryScreenshot = title.GetPrimaryMedia(MediaType.Screenshot);
        primaryScreenshot.ShouldNotBeNull();
        primaryScreenshot.SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void RecalculatePrimaryMedia_SamePriority_OldestWins()
    {
        var olderCover = CreateMedia(1, MediaType.Cover, "igdb",
            createdAt: DateTimeOffset.UtcNow.AddDays(-10));
        var newerCover = CreateMedia(2, MediaType.Cover, "igdb",
            createdAt: DateTimeOffset.UtcNow);

        var title = CreateTitle(media: [newerCover, olderCover]);

        title.RecalculatePrimaryMedia(["igdb"]);

        // Older one should win for stability
        var primary = title.GetPrimaryMedia(MediaType.Cover);
        primary.ShouldNotBeNull();
        primary.Id.ShouldBe(1);
    }

    [Fact]
    public void RecalculatePrimaryMedia_UnknownProvider_LowerThanKnown()
    {
        var unknownCover = CreateMedia(1, MediaType.Cover, "custom_provider",
            createdAt: DateTimeOffset.UtcNow.AddDays(-100));
        var igdbCover = CreateMedia(2, MediaType.Cover, "igdb",
            createdAt: DateTimeOffset.UtcNow);

        var title = CreateTitle(media: [unknownCover, igdbCover]);

        title.RecalculatePrimaryMedia(["igdb"]);

        // igdb wins over unknown provider
        var primary = title.GetPrimaryMedia(MediaType.Cover);
        primary.ShouldNotBeNull();
        primary.SourceId.ShouldBe("igdb");
    }

    [Fact]
    public void RecalculatePrimaryMedia_NoMedia_DoesNotThrow()
    {
        var title = CreateTitle();

        // Should not throw
        title.RecalculatePrimaryMedia(["igdb"]);

        title.GetPrimaryMedia(MediaType.Cover).ShouldBeNull();
    }

    [Fact]
    public void RecalculatePrimaryMedia_SingleMedia_BecomesPrimary()
    {
        var cover = CreateMedia(1, MediaType.Cover, "igdb");
        var title = CreateTitle(media: [cover]);

        title.RecalculatePrimaryMedia(["igdb"]);

        var primary = title.GetPrimaryMedia(MediaType.Cover);
        primary.ShouldNotBeNull();
        primary.Id.ShouldBe(1);
    }

    [Fact]
    public void RecalculatePrimaryMedia_ClearsPreviousPrimary()
    {
        // Start with igdb as primary
        var igdbCover = CreateMedia(1, MediaType.Cover, "igdb", isPrimary: true);
        var userCover = CreateMedia(2, MediaType.Cover, "user", isPrimary: false);

        var title = CreateTitle(media: [igdbCover, userCover]);

        title.RecalculatePrimaryMedia(["igdb"]);

        // User should now be primary, igdb should have been cleared
        var primary = title.GetPrimaryMedia(MediaType.Cover);
        primary.ShouldNotBeNull();
        primary.SourceId.ShouldBe("user");

        // Verify old primary was cleared
        var igdb = title.GetMediaByTypeAndSource(MediaType.Cover, "igdb");
        igdb.ShouldNotBeNull();
        igdb.IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public void RecalculatePrimaryMedia_FieldOverridesNotYetApplied()
    {
        // TODO: RecalculatePrimaryMedia does not currently consult per-field
        // screenshot preferences or platform-level media source overrides.
        // This test documents that limitation — media primary selection uses
        // only the global source priority, not field-level overrides.
        var igdbScreenshot = CreateMedia(1, MediaType.Screenshot, "igdb");
        var ssScreenshot = CreateMedia(2, MediaType.Screenshot, "screenscraper");

        var title = CreateTitle(media: [igdbScreenshot, ssScreenshot]);

        // Even with screenscraper first in priority, field overrides (if they existed)
        // would not affect RecalculatePrimaryMedia — it only uses sourcePriorityOrder
        title.RecalculatePrimaryMedia(["igdb", "screenscraper"]);

        var primary = title.GetPrimaryMedia(MediaType.Screenshot);
        primary.ShouldNotBeNull();
        primary.SourceId.ShouldBe("igdb"); // Global priority wins, no override mechanism here
    }
}
