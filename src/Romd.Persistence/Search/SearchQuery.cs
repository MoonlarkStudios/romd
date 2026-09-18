using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Search;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Search;

/// <summary>
///     Shared PostgreSQL full-text query construction (#173). User input is normalized exactly like
///     the persisted <c>SearchDocument</c>, then matched as one ordered phrase whose last token is a
///     prefix, so "mario bros" finds "Super Mario Bros." and "mar" finds every Mario title, while
///     operators and punctuation are literal text rather than query syntax. Input that leaves no
///     tokens (only punctuation or operators) matches nothing; blank input is the caller's
///     "no search" case and never reaches this class.
/// </summary>
public static class SearchQuery
{
    /// <summary>
    ///     Returns the <c>to_tsquery</c> text for the normalized tokens of <paramref name="text"/>,
    ///     or null when no token survives normalization.
    /// </summary>
    public static string? ToTsQueryText(string text)
    {
        var tokens = SearchTextNormalizer.Tokenize(text);
        if (tokens.Count == 0)
        {
            return null;
        }

        return string.Join(
            " <-> ",
            tokens.Select((token, index) => index == tokens.Count - 1 ? $"{token}:*" : token));
    }

    public static IQueryable<TitleEntity> MatchTitles(IQueryable<TitleEntity> titles, string text) =>
        ToTsQueryText(text) is { } tsQuery
            ? titles.Where(title => title.SearchVector.Matches(
                EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, tsQuery)))
            : titles.Where(_ => false);

    public static IQueryable<DatGameEntity> MatchGames(IQueryable<DatGameEntity> games, string text) =>
        ToTsQueryText(text) is { } tsQuery
            ? games.Where(game => game.SearchVector.Matches(
                EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, tsQuery)))
            : games.Where(_ => false);

    public static string FormatRank(float rank) => rank.ToString("R", CultureInfo.InvariantCulture);

    public static bool TryParseRank(string? value, out float rank) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rank);
}
