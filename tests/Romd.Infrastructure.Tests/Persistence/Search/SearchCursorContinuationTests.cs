using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Romd.Domain.Catalog;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence.Search;

public sealed class SearchCursorContinuationTests(SearchGoldenFixture fixture) : IClassFixture<SearchGoldenFixture>
{
    [Theory]
    [InlineData(GameSortField.Name, null, 1)]
    [InlineData(GameSortField.Name, null, 7)]
    [InlineData(GameSortField.Year, null, 1)]
    [InlineData(GameSortField.Year, null, 7)]
    [InlineData(GameSortField.Relevance, "mario", 1)]
    [InlineData(GameSortField.Relevance, "mario", 3)]
    public async Task Games_Continuation_VisitsEveryOrderedRowOnce(GameSortField sort, string? query, int size)
    {
        await using var db = fixture.CreateContext();
        var repository = new SearchRepository(db);
        var filters = new GameSearchFilters(BiosFilter: BiosFilter.Include);
        int[] expected = (await repository.SearchGamesAsync(query, filters, sort, null, 100, null)).Value.Items.Select(x => x.Id).ToArray();
        var actual = new List<int>();
        string? cursor = null;
        do
        {
            var page = (await repository.SearchGamesAsync(query, filters, sort, cursor, size, null)).Value;
            page.Items.ShouldNotBeEmpty();
            page.HasNextPage.ShouldBe(page.NextCursor is not null);
            actual.AddRange(page.Items.Select(x => x.Id));
            actual.Count.ShouldBeLessThanOrEqualTo(expected.Length);
            cursor = page.NextCursor;
        } while (cursor is not null);
        actual.ShouldBe(expected);
        actual.Distinct().Count().ShouldBe(expected.Length);
        if (sort == GameSortField.Name)
            actual.IndexOf(106).ShouldBeLessThan(actual.IndexOf(107)); // Equal Dr. Mario names use ascending IDs.
    }

    [Theory]
    [InlineData(TitleSortField.Name, null, 1)]
    [InlineData(TitleSortField.Name, null, 7)]
    [InlineData(TitleSortField.Rating, null, 1)]
    [InlineData(TitleSortField.Rating, null, 7)]
    [InlineData(TitleSortField.Relevance, "mario", 1)]
    [InlineData(TitleSortField.Relevance, "mario", 3)]
    public async Task Titles_Continuation_VisitsEveryOrderedRowOnce(TitleSortField sort, string? query, int size)
    {
        await using var db = fixture.CreateContext();
        var repository = new SearchRepository(db);
        int[] expected = (await repository.SearchTitlesAsync(query, new(), sort, null, 100, null)).Value.Items.Select(x => x.Id).ToArray();
        var actual = new List<int>();
        string? cursor = null;
        do
        {
            var page = (await repository.SearchTitlesAsync(query, new(), sort, cursor, size, null)).Value;
            page.Items.ShouldNotBeEmpty();
            actual.AddRange(page.Items.Select(x => x.Id));
            actual.Count.ShouldBeLessThanOrEqualTo(expected.Length);
            cursor = page.NextCursor;
        } while (cursor is not null);
        actual.ShouldBe(expected);
        actual.Distinct().Count().ShouldBe(expected.Length);
        if (sort == TitleSortField.Name)
            actual.IndexOf(6).ShouldBeLessThan(actual.IndexOf(7));
        if (sort == TitleSortField.Rating)
        {
            actual.IndexOf(11).ShouldBeLessThan(actual.IndexOf(12)); // Equal 8.8 ratings.
            actual[^1].ShouldBe(13); // NULL is after every non-null rating.
        }
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    public async Task NumericContinuations_AreInvariantAcrossRequestCultures(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            await using var db = fixture.CreateContext();
            var repository = new SearchRepository(db);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var rating = (await repository.SearchTitlesAsync(null, new(), TitleSortField.Rating, null, 1, null)).Value;
            var titles = (await repository.SearchTitlesAsync("mario", new(), TitleSortField.Relevance, null, 1, null)).Value;
            var games = (await repository.SearchGamesAsync("mario", new(), GameSortField.Relevance, null, 1, null)).Value;
            int ratingNext = (await repository.SearchTitlesAsync(null, new(), TitleSortField.Rating, rating.NextCursor, 1, null)).Value.Items.Single().Id;
            int titleNext = (await repository.SearchTitlesAsync("mario", new(), TitleSortField.Relevance, titles.NextCursor, 1, null)).Value.Items.Single().Id;
            int gameNext = (await repository.SearchGamesAsync("mario", new(), GameSortField.Relevance, games.NextCursor, 1, null)).Value.Items.Single().Id;

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            (await repository.SearchTitlesAsync(null, new(), TitleSortField.Rating, rating.NextCursor, 1, null)).Value.Items.Single().Id.ShouldBe(ratingNext);
            (await repository.SearchTitlesAsync("mario", new(), TitleSortField.Relevance, titles.NextCursor, 1, null)).Value.Items.Single().Id.ShouldBe(titleNext);
            (await repository.SearchGamesAsync("mario", new(), GameSortField.Relevance, games.NextCursor, 1, null)).Value.Items.Single().Id.ShouldBe(gameNext);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task FreeTextYearAndNullTies_ContinueWithoutRestarting()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.DatGames.Where(x => x.Id == 101).ExecuteUpdateAsync(s => s.SetProperty(x => x.Year, ""));
        await db.DatGames.Where(x => x.Id == 102).ExecuteUpdateAsync(s => s.SetProperty(x => x.Year, (string?)null));
        var repository = new SearchRepository(db);
        var filters = new GameSearchFilters(BiosFilter: BiosFilter.Include);
        int[] expected = (await repository.SearchGamesAsync(null, filters, GameSortField.Year, null, 100, null)).Value.Items.Select(x => x.Id).ToArray();
        var actual = new List<int>();
        string? cursor = null;
        do
        {
            var page = (await repository.SearchGamesAsync(null, filters, GameSortField.Year, cursor, 1, null)).Value;
            actual.AddRange(page.Items.Select(x => x.Id));
            actual.Count.ShouldBeLessThanOrEqualTo(expected.Length);
            cursor = page.NextCursor;
        } while (cursor is not null);
        actual.ShouldBe(expected);
        actual.TakeLast(3).ShouldBe([101, 102, SearchGoldenFixture.BiosDatGameId]);
    }

    [Fact]
    public async Task MaximumUnicodeNames_BothActualDatabaseContinuations_KeepExactTiePosition()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        string name = string.Concat(Enumerable.Repeat("\U0001F4BF", 500));
        foreach (var title in await db.Titles.AsTracking().Where(x => x.Id == 6 || x.Id == 7).ToArrayAsync())
        {
            title.Name = name;
            title.NormalizedName = name;
        }
        foreach (var game in await db.DatGames.AsTracking().Where(x => x.Id == 106 || x.Id == 107).ToArrayAsync())
            game.Name = name;
        await db.SaveChangesAsync();
        var repository = new SearchRepository(db);
        // Seek directly to the first of the equal names, as a real preceding page does.
        string gameCursor = SearchCursors.EncodeGames(new(GameSortField.Name, name, 106), SearchCursors.GamesFingerprint(null, new(), GameSortField.Name, null));
        string titleCursor = SearchCursors.EncodeTitles(new((int)TitleSortField.Name, name, 6), SearchCursors.TitlesFingerprint(null, new(), TitleSortField.Name, null));
        (await repository.SearchGamesAsync(null, new(), GameSortField.Name, gameCursor, 1, null)).Value.Items.Single().Id.ShouldBe(107);
        (await repository.SearchTitlesAsync(null, new(), TitleSortField.Name, titleCursor, 1, null)).Value.Items.Single().Id.ShouldBe(7);
    }

    [Fact]
    public async Task BothFamilies_ChangedQueryFiltersSortOrVisibility_ReturnFieldErrors()
    {
        await using var db = fixture.CreateContext();
        var repository = new SearchRepository(db);
        var game = (await repository.SearchGamesAsync(null, new(), GameSortField.Name, null, 1, null)).Value.NextCursor.ShouldNotBeNull();
        var title = (await repository.SearchTitlesAsync(null, new(), TitleSortField.Name, null, 1, null)).Value.NextCursor.ShouldNotBeNull();
        foreach (var result in new[] {
                     await repository.SearchGamesAsync("mario", new(), GameSortField.Name, game, 1, null),
                     await repository.SearchGamesAsync(null, new(Manufacturer: "Nintendo"), GameSortField.Name, game, 1, null),
                     await repository.SearchGamesAsync(null, new(), GameSortField.Year, game, 1, null),
                     await repository.SearchGamesAsync(null, new(), GameSortField.Name, game, 1, fixture.LibraryId) })
            FieldErrors.FieldOf(result.FirstError).ShouldBe("cursor");
        foreach (var result in new[] {
                     await repository.SearchTitlesAsync("mario", new(), TitleSortField.Name, title, 1, null),
                     await repository.SearchTitlesAsync(null, new(Tracked: TrackedFilter.Tracked), TitleSortField.Name, title, 1, null),
                     await repository.SearchTitlesAsync(null, new(), TitleSortField.Rating, title, 1, null),
                     await repository.SearchTitlesAsync(null, new(), TitleSortField.Name, title, 1, fixture.LibraryId) })
            FieldErrors.FieldOf(result.FirstError).ShouldBe("cursor");
    }

    [Fact]
    public async Task LibraryScopedContinuations_KeepExactVisibleMembership_AndEquivalentQueriesContinue()
    {
        await using var db = fixture.CreateContext();
        var repository = new SearchRepository(db);
        var gameIds = new List<int>();
        var titleIds = new List<int>();
        string? cursor = null;
        do
        {
            var page = (await repository.SearchGamesAsync(null, new(), GameSortField.Name, cursor, 1, fixture.LibraryId)).Value;
            gameIds.AddRange(page.Items.Select(x => x.Id));
            gameIds.Count.ShouldBeLessThanOrEqualTo(4);
            cursor = page.NextCursor;
        } while (cursor is not null);
        gameIds.Order().ShouldBe([101, 102, 103, 104]);
        do
        {
            var page = (await repository.SearchTitlesAsync(null, new(), TitleSortField.Name, cursor, 2, fixture.LibraryId)).Value;
            titleIds.AddRange(page.Items.Select(x => x.Id));
            titleIds.Count.ShouldBeLessThanOrEqualTo(24);
            cursor = page.NextCursor;
        } while (cursor is not null);
        titleIds.Order().ShouldBe(Enumerable.Range(1, 26).Except([13, 26]));

        var firstGames = (await repository.SearchGamesAsync("Pokémon", new(), GameSortField.Relevance, null, 1, null)).Value;
        var firstTitles = (await repository.SearchTitlesAsync("Pokémon", new(), TitleSortField.Relevance, null, 1, null)).Value;
        var nextGames = (await repository.SearchGamesAsync("pokemon", new(), GameSortField.Relevance, firstGames.NextCursor.ShouldNotBeNull(), 1, null)).Value;
        var nextTitles = (await repository.SearchTitlesAsync("pokemon", new(), TitleSortField.Relevance, firstTitles.NextCursor.ShouldNotBeNull(), 1, null)).Value;
        firstGames.Items.Concat(nextGames.Items).Select(x => x.Id).Order().ShouldBe([111, 112]);
        firstTitles.Items.Concat(nextTitles.Items).Select(x => x.Id).Order().ShouldBe([11, 12]);
    }
}
