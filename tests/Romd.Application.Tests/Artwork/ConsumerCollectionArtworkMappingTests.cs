using NSubstitute;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Collections.Queries.GetConsumerCollection;
using Romd.Consumer.Application.Collections.Queries.ListConsumerCollectionTitles;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class ConsumerCollectionArtworkMappingTests
{
    private static readonly Guid UserId = Guid.Parse("9f530e51-41d6-461c-af56-f53683af12d6");

    [Fact]
    public async Task HandleAsync_CollectionTitle_UsesResolvedRoleArtwork()
    {
        // Arrange
        var original = new ArtworkVariant(1, "source", "image/png", 1200, 1800, "original");
        var delivery = new ArtworkVariant(2, "delivery-v1", "image/webp", 600, 900, "poster");
        var asset = new ArtworkAsset(12, 5, ArtworkRole.Poster, "steamgriddb", "game", "asset",
            original, [delivery], true, DateTimeOffset.UnixEpoch);
        var title = new ConsumerCollectionTitleReadModel(5, "Title", 3, new Romd.Application.Common.Systems.SystemSummaryData("snes", "Platform", "SNES"), null,
            null, null, null, 1, 8, 0)
        {
            Artwork =
            [
                ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(5, ArtworkRole.Poster), [asset], []),
                ArtworkResolver.Resolve(ArtworkSelection.CreateAutomatic(5, ArtworkRole.Hero), [], [])
            ]
        };
        var repository = Substitute.For<IConsumerCollectionReadRepository>();
        repository.GetCollectionTitlesAsync(Arg.Any<ConsumerLibraryScope>(), 1,
                Arg.Any<ConsumerCollectionTitleCursor?>(), Arg.Any<ConsumerReleasePreference>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.Found(7, [title]));
        var settings = Substitute.For<IConsumerUserSettingsStore>();
        settings.GetSettingsAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new ConsumerAccountSettings("system", ConsumerReleasePreference.Default));
        var handler = new ListConsumerCollectionTitlesQueryHandler(CurrentUser(), settings, repository);

        // Act
        var result = await handler.HandleAsync(new ListConsumerCollectionTitlesQuery(1));

        // Assert
        result.IsError.ShouldBeFalse();
        var item = result.Value.Items.Single();
        item.Artwork.Count.ShouldBe(2);
        var poster = item.Artwork.Single(artwork => artwork.Role == "Poster");
        poster.AssetId.ShouldBe(IdCoder.Encode(12));
        poster.Url.ShouldBe($"/artwork/{IdCoder.Encode(12)}/poster/delivery-v1");
        poster.Width.ShouldBe(600);
        poster.OriginalWidth.ShouldBe(1200);
        poster.Fit.ShouldBe("Contain");
        item.Artwork.Single(artwork => artwork.Role == "Hero").FallbackReason.ShouldBe("NoArtwork");
        item.DefaultReleaseId.ShouldBe(IdCoder.Encode(8));
    }

    [Fact]
    public async Task HandleAsync_CollectionSummary_PreservesCustomCollectionCover()
    {
        // Arrange
        var repository = Substitute.For<IConsumerCollectionReadRepository>();
        repository.GetCollectionByIdAsync(Arg.Any<ConsumerLibraryScope>(), 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerCollectionReadModel>.Found(7,
                new ConsumerCollectionReadModel(1, "Collection", null, 42, null, null, 1)));
        var handler = new GetConsumerCollectionQueryHandler(CurrentUser(), repository);

        // Act
        var result = await handler.HandleAsync(new GetConsumerCollectionQuery(1));

        // Assert
        result.IsError.ShouldBeFalse();
        result.Value.CoverUrl.ShouldBe($"/media/{IdCoder.Encode(42)}");
        result.Value.HeroUrl.ShouldBeNull();
    }

    private static ICurrentUser CurrentUser()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(UserId);
        return currentUser;
    }
}
