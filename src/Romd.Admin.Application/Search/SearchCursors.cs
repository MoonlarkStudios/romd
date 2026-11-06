using System.Globalization;
using System.Text;
using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Search;
using Romd.Application.Common.Validation;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Search continuations bind the effective query, filters, visibility and exact ordering.
///     Old unbound cursors cannot establish that scope: callers must restart without a cursor.
/// </summary>
public static class SearchCursors
{
    // UTF-8 text positions are base64 inside the envelope. Even 500 astral code points fit
    // below this cap and an ordinary HTTP request line, independent of JSON Unicode escaping.
    public const int MaxLength = 4_096;
    public const int MaxLimit = 100;

    public static GameSearchFilters Normalize(GameSearchFilters filters) => filters with
    {
        Year = Active(filters.Year),
        Manufacturer = Active(filters.Manufacturer)
    };

    public static TitleSearchFilters Normalize(TitleSearchFilters filters) => filters with
    {
        Genre = Active(filters.Genre),
        EnrichmentStatus = Active(filters.EnrichmentStatus),
        ArtworkStatus = Active(filters.ArtworkStatus),
        Enrichment = filters.Enrichment is { Count: > 0 } flags ? flags.Distinct().Order().ToArray() : null
    };

    public static string GamesFingerprint(string? query, GameSearchFilters filters, GameSortField sort, int? libraryId) =>
        KeysetCursor.Fingerprint("admin.search.games.v1", !string.IsNullOrWhiteSpace(query), SearchTextNormalizer.Normalize(query),
            Normalize(filters), libraryId, sort, "name:asc/year:desc-null-last/rank:desc,id:asc");

    public static string TitlesFingerprint(string? query, TitleSearchFilters filters, TitleSortField sort, int? libraryId) =>
        KeysetCursor.Fingerprint("admin.catalog.v1", !string.IsNullOrWhiteSpace(query), SearchTextNormalizer.Normalize(query),
            Normalize(filters), libraryId, sort, "name:asc/rating:desc-null-last/rank:desc,id:asc");

    public static string EncodeGames(CompositeCursor position, string fingerprint) => KeysetCursor.Encode(
        position.SortField is GameSortField.Name or GameSortField.Year
            ? position with { SortValue = EncodeText(position.SortValue) } : position, fingerprint);

    public static string EncodeTitles(TitleCompositeCursor position, string fingerprint) => KeysetCursor.Encode(
        position.SortField == (int)TitleSortField.Name
            ? position with { SortValue = EncodeText(position.SortValue) } : position, fingerprint);

    public static ErrorOr<CompositeCursor?> ReadGames(string? query, GameSearchFilters filters, GameSortField sort,
        string? cursor, int limit, int? libraryId)
    {
        var errors = ValidatePage(limit, Enum.IsDefined(sort), sort == GameSortField.Relevance, query);
        if (errors.Count > 0)
            return errors;
        var result = KeysetCursor.Decode<CompositeCursor>(cursor, GamesFingerprint(query, filters, sort, libraryId),
            position => position.Id > 0 && position.SortField == sort && (sort switch
            {
                GameSortField.Name => position.SortValue is not null && IsText(position.SortValue, 500),
                // DAT years are free text, including empty or missing values, not numeric years.
                GameSortField.Year => IsText(position.SortValue, 10),
                GameSortField.Relevance => IsCanonicalRank(position.SortValue),
                _ => false
            }), MaxLength);
        if (result.IsError)
            return SearchErrors.InvalidCursor;
        return result.Value is { SortField: GameSortField.Name or GameSortField.Year } text
            ? text with { SortValue = DecodeText(text.SortValue) } : result;
    }

    public static ErrorOr<TitleCompositeCursor?> ReadTitles(string? query, TitleSearchFilters filters, TitleSortField sort,
        string? cursor, int limit, int? libraryId)
    {
        var errors = ValidatePage(limit, Enum.IsDefined(sort), sort == TitleSortField.Relevance, query);
        if (errors.Count > 0)
            return errors;
        var result = KeysetCursor.Decode<TitleCompositeCursor>(cursor, TitlesFingerprint(query, filters, sort, libraryId),
            position => position.Id > 0 && position.SortField == (int)sort && (sort switch
            {
                TitleSortField.Name => position.SortValue is not null && IsText(position.SortValue, 500),
                TitleSortField.Rating => position.SortValue is null || IsCanonicalRating(position.SortValue),
                TitleSortField.Relevance => IsCanonicalRank(position.SortValue),
                _ => false
            }), MaxLength);
        if (result.IsError)
            return SearchErrors.InvalidCursor;
        return result.Value is { } text && text.SortField == (int)TitleSortField.Name
            ? text with { SortValue = DecodeText(text.SortValue) } : result;
    }

    private static List<Error> ValidatePage(int limit, bool validSort, bool relevance, string? query)
    {
        var errors = new List<Error>();
        if (limit is < 1 or > MaxLimit)
            errors.Add(FieldErrors.Validation("Search.InvalidLimit", $"Choose a limit from 1 to {MaxLimit}.", "limit"));
        if (!validSort)
            errors.Add(FieldErrors.Validation("Search.InvalidSort", "Choose a supported sort field.", "sortBy"));
        else if (relevance && string.IsNullOrWhiteSpace(query))
            errors.Add(SearchErrors.RelevanceSortRequiresQuery);
        return errors;
    }

    private static bool IsCanonicalRank(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rank) && float.IsFinite(rank) && rank >= 0
        && rank.ToString("R", CultureInfo.InvariantCulture) == value;

    private static bool IsCanonicalRating(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rating) && double.IsFinite(rating)
        && rating.ToString("R", CultureInfo.InvariantCulture) == value;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static string? EncodeText(string? text) => text is null ? null
        : Convert.ToBase64String(StrictUtf8.GetBytes(text)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string? DecodeText(string? value) => value is null ? null
        : StrictUtf8.GetString(Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
            .PadRight((value.Length + 3) / 4 * 4, '=')));

    private static bool IsText(string? value, int maxCharacters)
    {
        if (value is null)
            return true;
        try
        {
            string text = DecodeText(value)!;
            return EncodeText(text) == value && !text.Contains('\0') && text.EnumerateRunes().Count() <= maxCharacters;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    // Equality filters keep significant spelling/spacing; SQL ignores only whitespace-only values.
    private static string? Active(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
