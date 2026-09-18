using ErrorOr;
using NSubstitute;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.Queries.GetConsumerPlatform;
using Romd.Consumer.Application.Browse.Queries.GetConsumerTitle;
using Romd.Consumer.Application.Browse.Queries.ListConsumerPlatforms;
using Romd.Consumer.Application.Browse.Queries.SearchConsumerCatalog;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Collections.Queries.GetConsumerCollection;
using Romd.Consumer.Application.Collections.Queries.ListConsumerCollections;
using Romd.Consumer.Application.Collections.Queries.ListConsumerCollectionTitles;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Libraries.Queries.GetCurrentLibraryContext;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer;

public sealed class ConsumerLibraryOutcomeHandlerTests
{
    private const int LibraryId = 17;
    private static readonly Guid UserId = Guid.Parse("79b5b410-8e99-4df5-91ce-4b8861dc2416");

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.LibraryNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.LibraryNotFound")]
    public async Task CurrentLibraryContext_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerLibraryContextRepository>();
        var value = new ConsumerLibraryContextReadModel(
            "Living Room",
            new ConsumerLibraryContentCountsReadModel(0, 0, 0),
            [],
            [],
            []);
        repository.GetCurrentContextAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new GetCurrentLibraryContextQueryHandler(CurrentUser(), repository);

        var result = await handler.HandleAsync(new GetCurrentLibraryContextQuery());

        AssertOutcome(result, expectedError);
    }

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.LibraryNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.LibraryNotFound")]
    public async Task PlatformList_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerBrowseRepository>();
        var value = new PagedList<ConsumerPlatformSummaryData>([], null, false);
        repository.ListPlatformsAsync(
                Arg.Any<ConsumerLibraryScope>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new ListConsumerPlatformsQueryHandler(CurrentUser(), repository);

        var result = await handler.HandleAsync(new ListConsumerPlatformsQuery(null, 20));

        AssertOutcome(result, expectedError);
    }

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.PlatformNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.PlatformNotFound")]
    public async Task PlatformDetail_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerBrowseRepository>();
        var value = new ConsumerPlatformDetailData
        {
            Key = "snes",
            Id = 1,
            Name = "SNES",
            ShortName = "snes",
            TitleCount = 0,
            Media = []
        };
        repository.GetPlatformAsync(
                Arg.Any<ConsumerLibraryScope>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new GetConsumerPlatformQueryHandler(CurrentUser(), repository);

        var result = await handler.HandleAsync(new GetConsumerPlatformQuery(1));

        AssertOutcome(result, expectedError);
    }

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.LibraryNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.LibraryNotFound")]
    public async Task CatalogSearch_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerBrowseRepository>();
        var value = new PagedList<ConsumerTitleCardData>([], null, false);
        repository.SearchCatalogAsync(
                Arg.Any<ConsumerLibraryScope>(),
                Arg.Any<ConsumerCatalogFilters>(),
                Arg.Any<ConsumerReleasePreference>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new SearchConsumerCatalogQueryHandler(CurrentUser(), SettingsStore(), repository);

        var result = await handler.HandleAsync(
            new SearchConsumerCatalogQuery(null, null, null, null, null, null, 20));

        AssertOutcome(result, expectedError);
    }

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.TitleNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.TitleNotFound")]
    public async Task TitleDetail_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerBrowseRepository>();
        var value = new ConsumerTitleDetailData
        {
            Id = 1,
            PlatformId = 1,
            System = new Romd.Application.Common.Systems.SystemSummaryData("snes", "SNES", "SNES"),
            Name = "Game",
            Media = [],
            Releases = []
        };
        repository.GetTitleAsync(
                Arg.Any<ConsumerLibraryScope>(),
                Arg.Any<int>(),
                Arg.Any<ConsumerReleasePreference>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new GetConsumerTitleQueryHandler(CurrentUser(), SettingsStore(), repository);

        var result = await handler.HandleAsync(new GetConsumerTitleQuery(1));

        AssertOutcome(result, expectedError);
    }

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.LibraryNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.LibraryNotFound")]
    public async Task CollectionList_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerCollectionReadRepository>();
        IReadOnlyList<ConsumerCollectionReadModel> value = [];
        repository.GetCollectionsAsync(
                Arg.Any<ConsumerLibraryScope>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new ListConsumerCollectionsQueryHandler(CurrentUser(), repository);

        var result = await handler.HandleAsync(new ListConsumerCollectionsQuery());

        AssertOutcome(result, expectedError);
    }

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.CollectionNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.CollectionNotFound")]
    public async Task CollectionDetail_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerCollectionReadRepository>();
        var value = new ConsumerCollectionReadModel(1, "Favorites", null, null, null, null, 0);
        repository.GetCollectionByIdAsync(
                Arg.Any<ConsumerLibraryScope>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new GetConsumerCollectionQueryHandler(CurrentUser(), repository);

        var result = await handler.HandleAsync(new GetConsumerCollectionQuery(1));

        AssertOutcome(result, expectedError);
    }

    [Theory]
    [InlineData("found", null)]
    [InlineData("item-not-found", "Consumer.CollectionNotFound")]
    [InlineData("unavailable", "Consumer.LibraryNotFound")]
    [InlineData("inconsistent", "Consumer.CollectionNotFound")]
    public async Task CollectionTitles_MapsEveryOutcome(string outcome, string? expectedError)
    {
        var repository = Substitute.For<IConsumerCollectionReadRepository>();
        IReadOnlyList<ConsumerCollectionTitleReadModel> value = [];
        repository.GetCollectionTitlesAsync(
                Arg.Any<ConsumerLibraryScope>(),
                Arg.Any<int>(),
                Arg.Any<ConsumerCollectionTitleCursor?>(),
                Arg.Any<ConsumerReleasePreference>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(outcome, value));
        var handler = new ListConsumerCollectionTitlesQueryHandler(CurrentUser(), SettingsStore(), repository);

        var result = await handler.HandleAsync(new ListConsumerCollectionTitlesQuery(1));

        AssertOutcome(result, expectedError);
    }

    private static ConsumerLibraryReadResult<T> Result<T>(string outcome, T value)
        where T : notnull =>
        outcome switch
        {
            "found" => new ConsumerLibraryReadResult<T>.Found(LibraryId, value),
            "item-not-found" => new ConsumerLibraryReadResult<T>.ItemNotFound(LibraryId),
            "unavailable" => new ConsumerLibraryReadResult<T>.LibraryUnavailable(),
            "inconsistent" => new ConsumerLibraryReadResult<T>.ProjectionInconsistent(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };

    private static ICurrentUser CurrentUser()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(UserId);
        return currentUser;
    }

    private static IConsumerUserSettingsStore SettingsStore()
    {
        var settings = Substitute.For<IConsumerUserSettingsStore>();
        settings.GetSettingsAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new ConsumerAccountSettings("system", ConsumerReleasePreference.Default));
        return settings;
    }

    private static void AssertOutcome<T>(ErrorOr<T> result, string? expectedError)
    {
        if (expectedError is null)
        {
            result.IsError.ShouldBeFalse();
            return;
        }

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(expectedError);
    }
}
