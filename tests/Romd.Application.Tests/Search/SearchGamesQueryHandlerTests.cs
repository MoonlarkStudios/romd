using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Search;
using Romd.Admin.Application.Search.Queries.SearchGames;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Application.Common.Validation;
using Romd.Domain.Identity;
using Romd.Domain.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Search;

public sealed class SearchGamesQueryHandlerTests
{
    [Theory]
    [InlineData("bad", 50, GameSortField.Name, null, "cursor")]
    [InlineData(null, 0, GameSortField.Name, null, "limit")]
    [InlineData(null, 101, GameSortField.Name, null, "limit")]
    [InlineData(null, 50, (GameSortField)99, null, "sortBy")]
    [InlineData(null, 50, GameSortField.Relevance, " ", "sortBy")]
    public async Task HandleAsync_InvalidInput_RejectsBeforeRead(string? cursor, int limit, GameSortField sort, string? query, string field)
    {
        var repository = Substitute.For<ISearchRepository>();
        var user = Substitute.For<ICurrentUser>();
        var result = await new SearchGamesQueryHandler(repository, user).HandleAsync(new() { Cursor = cursor, Limit = limit, SortBy = sort, Query = query });

        result.IsError.ShouldBeTrue();
        FieldErrors.FieldOf(result.FirstError).ShouldBe(field);
        repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_VisibilityChanged_RejectsCursorBeforeRead()
    {
        var repository = Substitute.For<ISearchRepository>();
        var user = Substitute.For<ICurrentUser>();
        user.LibraryId.Returns(2);
        string cursor = SearchCursors.EncodeGames(new(GameSortField.Name, "A", 1), SearchCursors.GamesFingerprint(null, new(), GameSortField.Name, 1));

        var result = await new SearchGamesQueryHandler(repository, user).HandleAsync(new() { Cursor = cursor });

        result.FirstError.Code.ShouldBe("Search.InvalidCursor");
        repository.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, null)]
    public async Task HandleAsync_ValidContinuation_PreservesScopeAndCancellation(bool contributor, int? expectedLibrary)
    {
        var repository = Substitute.For<ISearchRepository>();
        var user = Substitute.For<ICurrentUser>();
        user.LibraryId.Returns(7);
        user.HasRole(RomdRoleType.Contributor).Returns(contributor);
        string cursor = SearchCursors.EncodeGames(new(GameSortField.Name, "A", 1), SearchCursors.GamesFingerprint("mario", new(), GameSortField.Name, expectedLibrary));
        var page = new PagedList<DatGame>([], null, false);
        repository.SearchGamesAsync(Arg.Any<string?>(), Arg.Any<GameSearchFilters>(), Arg.Any<GameSortField>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<PagedList<DatGame>>>(page));
        using var cancellation = new CancellationTokenSource();

        var result = await new SearchGamesQueryHandler(repository, user).HandleAsync(new() { Query = "mario", Cursor = cursor }, cancellation.Token);

        result.Value.ShouldBeSameAs(page);
        await repository.Received(1).SearchGamesAsync("mario", new(), GameSortField.Name, cursor, 50, expectedLibrary, cancellation.Token);
    }
}
