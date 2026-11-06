using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles.Enrichment;

public sealed class TitleMetadataRematerializerTests
{
    private readonly EnrichmentOptions _options = new() { GlobalSourcePriority = ["igdb", "screenscraper"] };
    private readonly IPlatformFieldDefaultRepository _platformDefaults = Substitute.For<IPlatformFieldDefaultRepository>();

    private TitleMetadataRematerializer CreateRematerializer() => new(_platformDefaults, Options.Create(_options));

    private static Title CreateTitle() => Title.Rehydrate(
        1, 10, "Test Title", "test title", null, null, null, null, null, null, null,
        EnrichmentStatus.None, null, DateTimeOffset.UtcNow,
        metadataLayers:
        [
            TitleMetadataLayer.CreateNew(1, "igdb", MetadataSourceType.Provider,
                new TitleMetadataPayload { Description = "IGDB desc", Genre = "Platformer" }, updatedAt: DateTimeOffset.UnixEpoch),
            TitleMetadataLayer.CreateNew(1, "screenscraper", MetadataSourceType.Provider,
                new TitleMetadataPayload { Description = "SS desc", Genre = "Action" }, updatedAt: DateTimeOffset.UnixEpoch)
        ]);

    [Fact]
    public async Task RematerializeAsync_PlatformDefault_OverridesGlobalPriorityForItsField()
    {
        var title = CreateTitle();
        _platformDefaults.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Genre"] = "screenscraper" });

        await CreateRematerializer().RematerializeAsync(title);

        title.Genre.ShouldBe("Action");
        title.FieldProvenance["Genre"].ShouldBe("screenscraper");
        title.Description.ShouldBe("IGDB desc");
        title.FieldProvenance["Description"].ShouldBe("igdb");
    }

    [Fact]
    public async Task RematerializeAsync_NoPlatformDefaults_UsesGlobalPriorityAndKeepsName()
    {
        var title = CreateTitle();
        _platformDefaults.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        await CreateRematerializer().RematerializeAsync(title);

        title.Description.ShouldBe("IGDB desc");
        title.Genre.ShouldBe("Platformer");
        title.Name.ShouldBe("Test Title");
    }

    [Fact]
    public async Task RematerializeAsync_InvalidRetainedRating_LeavesPreviousEffectiveMetadataIntact()
    {
        var title = CreateTitle();
        _platformDefaults.GetByPlatformIdAsync(10, Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string>());
        await CreateRematerializer().RematerializeAsync(title);
        title.StoreProviderLayer("igdb", MetadataSourceType.Provider,
            new TitleMetadataPayload { Description = "Invalid replacement", ContentRatings = [null!] }, updatedAt: DateTimeOffset.UnixEpoch);

        await Should.ThrowAsync<ArgumentNullException>(() => CreateRematerializer().RematerializeAsync(title));

        title.Description.ShouldBe("IGDB desc");
        title.Genre.ShouldBe("Platformer");
        title.FieldProvenance[nameof(Title.Description)].ShouldBe("igdb");
        title.ContentRatingsMaterialized.ShouldBeTrue();
        title.Name.ShouldBe("Test Title");
    }
}
