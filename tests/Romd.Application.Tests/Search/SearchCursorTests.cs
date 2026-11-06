using Romd.Admin.Application.Search;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Search;

public sealed class SearchCursorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("199?")]
    public void Games_YearPosition_PreservesNullableFreeText(string? year)
    {
        var position = new CompositeCursor(GameSortField.Year, year, 7);
        string cursor = SearchCursors.EncodeGames(position, Games(GameSortField.Year));
        SearchCursors.ReadGames(null, new(), GameSortField.Year, cursor, 1, null).Value.ShouldBe(position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("日本語 / Pokémon + Σ")]
    public void Names_BothSearchFamilies_RoundTripExactSortFacts(string name)
    {
        var game = new CompositeCursor(GameSortField.Name, name, 7);
        var title = new TitleCompositeCursor((int)TitleSortField.Name, name, 8);
        SearchCursors.ReadGames(null, new(), GameSortField.Name,
            SearchCursors.EncodeGames(game, Games()), 100, null).Value.ShouldBe(game);
        SearchCursors.ReadTitles(null, new(), TitleSortField.Name,
            SearchCursors.EncodeTitles(title, Titles()), 100, null).Value.ShouldBe(title);
    }

    [Fact]
    public void MaximumUnicodeName_CanonicalCursorAndEncodedRequestFitWireBudgets()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F4BF", 500));
        var game = new CompositeCursor(GameSortField.Name, name, int.MaxValue);
        var title = new TitleCompositeCursor((int)TitleSortField.Name, name, int.MaxValue);
        foreach (string cursor in new[] { SearchCursors.EncodeGames(game, Games()), SearchCursors.EncodeTitles(title, Titles()) })
        {
            cursor.Length.ShouldBeLessThan(SearchCursors.MaxLength);
            Uri.EscapeDataString(cursor).ShouldBe(cursor);
            // Even a 2-KiB query/filter suffix leaves this actual worst-width name below Kestrel's 8-KiB line budget.
            ("GET /api/search/games?cursor=" + cursor + "&query=" + new string('q', 2048) + " HTTP/1.1\r\n").Length.ShouldBeLessThan(8192);
        }
        SearchCursors.ReadGames(null, new(), GameSortField.Name, SearchCursors.EncodeGames(game, Games()), 1, null).Value.ShouldBe(game);
        SearchCursors.ReadTitles(null, new(), TitleSortField.Name, SearchCursors.EncodeTitles(title, Titles()), 1, null).Value.ShouldBe(title);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-base64-json")]
    [InlineData("e30")]
    [InlineData("bnVsbA")]
    public void BothFamilies_MalformedCursor_IsFieldValidation(string cursor) => RejectBoth(cursor);

    [Fact]
    public void BothFamilies_OversizeNoncanonicalUnboundAndCrossFamilyCursors_AreRejected()
    {
        string game = SearchCursors.EncodeGames(new(GameSortField.Name, "A", 1), Games());
        string title = SearchCursors.EncodeTitles(new((int)TitleSortField.Name, "A", 1), Titles());
        foreach (string invalid in new[] { game + "=", game + "AA", new string('a', SearchCursors.MaxLength + 1),
                     CursorUtils.ToCursor(new CompositeCursor(GameSortField.Name, "A", 1)),
                     CursorUtils.ToCursor(new { Position = new CompositeCursor(GameSortField.Name, "QQ", 1), Fingerprint = Games(), Extra = true }) })
            RejectBoth(invalid);
        Invalid(SearchCursors.ReadGames(null, new(), GameSortField.Name, title, 1, null));
        Invalid(SearchCursors.ReadTitles(null, new(), TitleSortField.Name, game, 1, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BothFamilies_NonpositiveTieBreaker_IsRejected(int id)
    {
        Invalid(SearchCursors.ReadGames(null, new(), GameSortField.Name,
            SearchCursors.EncodeGames(new(GameSortField.Name, "A", id), Games()), 1, null));
        Invalid(SearchCursors.ReadTitles(null, new(), TitleSortField.Name,
            SearchCursors.EncodeTitles(new((int)TitleSortField.Name, "A", id), Titles()), 1, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("QQ==")]
    [InlineData("wA")]
    [InlineData("AA")]
    [InlineData("\ufffd")]
    public void BothFamilies_InvalidOrNoncanonicalInnerUtf8Name_IsRejected(string? encoded)
    {
        Invalid(SearchCursors.ReadGames(null, new(), GameSortField.Name,
            KeysetCursor.Encode(new CompositeCursor(GameSortField.Name, encoded, 1), Games()), 1, null));
        Invalid(SearchCursors.ReadTitles(null, new(), TitleSortField.Name,
            KeysetCursor.Encode(new TitleCompositeCursor((int)TitleSortField.Name, encoded, 1), Titles()), 1, null));
    }

    [Fact]
    public void SortPositions_OutsidePersistableTextOrRankRange_AreRejected()
    {
        Invalid(SearchCursors.ReadGames(null, new(), GameSortField.Name,
            SearchCursors.EncodeGames(new(GameSortField.Name, new string('a', 501), 1), Games()), 1, null));
        Invalid(SearchCursors.ReadTitles(null, new(), TitleSortField.Name,
            SearchCursors.EncodeTitles(new((int)TitleSortField.Name, new string('a', 501), 1), Titles()), 1, null));
        Invalid(SearchCursors.ReadGames(null, new(), GameSortField.Year,
            SearchCursors.EncodeGames(new(GameSortField.Year, "12345678901", 1), Games(GameSortField.Year)), 1, null));
        foreach (string? rank in new[] { null, "-1" })
        {
            Invalid(SearchCursors.ReadGames("game", new(), GameSortField.Relevance,
                SearchCursors.EncodeGames(new(GameSortField.Relevance, rank, 1), Games(GameSortField.Relevance, "game")), 1, null));
            Invalid(SearchCursors.ReadTitles("game", new(), TitleSortField.Relevance,
                SearchCursors.EncodeTitles(new((int)TitleSortField.Relevance, rank, 1), Titles(TitleSortField.Relevance, "game")), 1, null));
        }
    }

    [Fact]
    public void BothFamilies_PositionSortMustMatchEvenWithTheRequestedFingerprint()
    {
        Invalid(SearchCursors.ReadGames(null, new(), GameSortField.Name,
            SearchCursors.EncodeGames(new(GameSortField.Year, "1990", 1), Games()), 1, null));
        Invalid(SearchCursors.ReadTitles(null, new(), TitleSortField.Name,
            SearchCursors.EncodeTitles(new((int)TitleSortField.Rating, "9", 1), Titles()), 1, null));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1,5")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("+1")]
    [InlineData("invalid")]
    public void NumericPositions_RequireFiniteCanonicalInvariantNumbers(string value)
    {
        Invalid(SearchCursors.ReadGames("game", new(), GameSortField.Relevance,
            SearchCursors.EncodeGames(new(GameSortField.Relevance, value, 1), Games(GameSortField.Relevance, "game")), 1, null));
        Invalid(SearchCursors.ReadTitles("game", new(), TitleSortField.Relevance,
            SearchCursors.EncodeTitles(new((int)TitleSortField.Relevance, value, 1), Titles(TitleSortField.Relevance, "game")), 1, null));
        Invalid(SearchCursors.ReadTitles(null, new(), TitleSortField.Rating,
            SearchCursors.EncodeTitles(new((int)TitleSortField.Rating, value, 1), Titles(TitleSortField.Rating)), 1, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("9.125")]
    [InlineData("-2.5")]
    public void Titles_CanonicalFiniteOrNullRating_RoundTrips(string? value)
    {
        var position = new TitleCompositeCursor((int)TitleSortField.Rating, value, 7);
        SearchCursors.ReadTitles(null, new(), TitleSortField.Rating,
            SearchCursors.EncodeTitles(position, Titles(TitleSortField.Rating)), 1, null).Value.ShouldBe(position);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.06079271")]
    [InlineData("1.5")]
    public void BothFamilies_CanonicalNonnegativeRank_RoundTrips(string value)
    {
        var game = new CompositeCursor(GameSortField.Relevance, value, 1);
        var title = new TitleCompositeCursor((int)TitleSortField.Relevance, value, 1);
        SearchCursors.ReadGames("game", new(), GameSortField.Relevance,
            SearchCursors.EncodeGames(game, Games(GameSortField.Relevance, "game")), 1, null).Value.ShouldBe(game);
        SearchCursors.ReadTitles("game", new(), TitleSortField.Relevance,
            SearchCursors.EncodeTitles(title, Titles(TitleSortField.Relevance, "game")), 1, null).Value.ShouldBe(title);
    }

    [Fact]
    public void EveryActiveGameFilterQuerySortAndLibraryScope_IsBound()
    {
        var filters = new GameSearchFilters(1, "1990", "Nintendo", 2, Romd.Domain.Catalog.BiosFilter.Include);
        string cursor = SearchCursors.EncodeGames(new(GameSortField.Name, "A", 1), SearchCursors.GamesFingerprint("mario", filters, GameSortField.Name, 3));
        foreach (var changed in new[] { filters with { PlatformId = 2 }, filters with { Year = "1991" }, filters with { Manufacturer = "nintendo" },
                     filters with { RegionId = 1 }, filters with { BiosFilter = Romd.Domain.Catalog.BiosFilter.Only } })
            Invalid(SearchCursors.ReadGames("mario", changed, GameSortField.Name, cursor, 1, 3));
        Invalid(SearchCursors.ReadGames("zelda", filters, GameSortField.Name, cursor, 1, 3));
        Invalid(SearchCursors.ReadGames("mario", filters, GameSortField.Year, cursor, 1, 3));
        Invalid(SearchCursors.ReadGames("mario", filters, GameSortField.Name, cursor, 1, 4));
        Invalid(SearchCursors.ReadGames("mario", filters, GameSortField.Name, cursor, 1, null));
    }

    [Fact]
    public void EveryActiveTitleFilterQuerySortAndLibraryScope_IsBound()
    {
        var filters = new TitleSearchFilters(1, "Action", ReleaseCompletenessFilter.Partial, "Completed", TrackedFilter.Tracked,
            "Poster", [EnrichmentFlagFilter.Automatic], Romd.Admin.Application.TrackedCollection.TrackedCollectionView.Missing);
        string cursor = SearchCursors.EncodeTitles(new((int)TitleSortField.Name, "A", 1), SearchCursors.TitlesFingerprint("mario", filters, TitleSortField.Name, 3));
        foreach (var changed in new[] { filters with { PlatformId = 2 }, filters with { Genre = "Puzzle" }, filters with { ReleaseCompleteness = ReleaseCompletenessFilter.None },
                     filters with { EnrichmentStatus = "Failed" }, filters with { Tracked = TrackedFilter.Untracked }, filters with { ArtworkStatus = "Logo" },
                     filters with { Enrichment = [EnrichmentFlagFilter.Processing] }, filters with { ReleaseReview = Romd.Admin.Application.TrackedCollection.TrackedCollectionView.Upgrades } })
            Invalid(SearchCursors.ReadTitles("mario", changed, TitleSortField.Name, cursor, 1, 3));
        Invalid(SearchCursors.ReadTitles("zelda", filters, TitleSortField.Name, cursor, 1, 3));
        Invalid(SearchCursors.ReadTitles("mario", filters, TitleSortField.Rating, cursor, 1, 3));
        Invalid(SearchCursors.ReadTitles("mario", filters, TitleSortField.Name, cursor, 1, 4));
        Invalid(SearchCursors.ReadTitles("mario", filters, TitleSortField.Name, cursor, 1, null));
    }

    [Fact]
    public void Fingerprints_NormalizeOnlyEffectiveSemantics_AndKeepEmptySearchDistinctFromNoSearch()
    {
        SearchCursors.GamesFingerprint("Pokémon: Ｒed", new(Year: " ", Manufacturer: "\t"), GameSortField.Name, null)
            .ShouldBe(SearchCursors.GamesFingerprint("pokemon red", new(), GameSortField.Name, null));
        SearchCursors.TitlesFingerprint("pokemon red", new(Genre: " ", Enrichment: [EnrichmentFlagFilter.Blocked, EnrichmentFlagFilter.Automatic, EnrichmentFlagFilter.Blocked]), TitleSortField.Name, null)
            .ShouldBe(SearchCursors.TitlesFingerprint("Pokémon: Ｒed", new(Enrichment: [EnrichmentFlagFilter.Automatic, EnrichmentFlagFilter.Blocked]), TitleSortField.Name, null));
        SearchCursors.GamesFingerprint(null, new(), GameSortField.Name, null).ShouldBe(SearchCursors.GamesFingerprint(" ", new(), GameSortField.Name, null));
        SearchCursors.GamesFingerprint(null, new(), GameSortField.Name, null).ShouldNotBe(SearchCursors.GamesFingerprint("!!!", new(), GameSortField.Name, null));
        SearchCursors.TitlesFingerprint(null, new(), TitleSortField.Name, null).ShouldNotBe(SearchCursors.TitlesFingerprint("!!!", new(), TitleSortField.Name, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void BothFamilies_OutOfRangeLimit_IsFieldValidation(int limit)
    {
        FieldErrors.FieldOf(SearchCursors.ReadGames(null, new(), GameSortField.Name, null, limit, null).FirstError).ShouldBe("limit");
        FieldErrors.FieldOf(SearchCursors.ReadTitles(null, new(), TitleSortField.Name, null, limit, null).FirstError).ShouldBe("limit");
    }

    private static string Games(GameSortField sort = GameSortField.Name, string? query = null) => SearchCursors.GamesFingerprint(query, new(), sort, null);
    private static string Titles(TitleSortField sort = TitleSortField.Name, string? query = null) => SearchCursors.TitlesFingerprint(query, new(), sort, null);
    private static void RejectBoth(string cursor)
    {
        Invalid(SearchCursors.ReadGames(null, new(), GameSortField.Name, cursor, 1, null));
        Invalid(SearchCursors.ReadTitles(null, new(), TitleSortField.Name, cursor, 1, null));
    }
    private static void Invalid<T>(ErrorOr.ErrorOr<T> result)
    {
        result.IsError.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("Search.InvalidCursor");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("cursor");
    }
}
