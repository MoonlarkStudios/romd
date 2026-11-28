using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Search;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Search;

/// <summary>
///     Shared PostgreSQL full-text query construction. User input is normalized exactly like
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
    public static string? ToTsQueryText(string text, bool allowOmittedPossessive = false, bool prefixLastToken = true)
    {
        var tokens = SearchTextNormalizer.Tokenize(text);
        if (tokens.Count == 0)
        {
            return null;
        }

        var terms = tokens.ToArray();
        if (prefixLastToken)
            terms[^1] += ":*";
        string phrase = string.Join(" <-> ", terms);
        if (!allowOmittedPossessive || terms.Length < 2)
        {
            return phrase;
        }

        // The indexed form of "Link's Awakening" contains the separate possessive token "s".
        // Keep the ordered phrase and allow that token between its words for catalog title search.
        return string.Join(" <-> ", terms.Select((term, index) =>
            index == terms.Length - 1 || tokens[index + 1] == "s" || term == "s"
                ? term
                : $"({term} | ({term} <-> s))"));
    }

    public static IQueryable<TitleEntity> MatchTitles(
        IQueryable<TitleEntity> titles, IQueryable<TitleLookupNameEntity> lookupNames,
        string text, bool allowOmittedPossessive = false) =>
        MatchTitleQuery(titles, lookupNames, ToTsQueryText(text, allowOmittedPossessive));

    public static IQueryable<TitleEntity> MatchTitleQuery(
        IQueryable<TitleEntity> titles, IQueryable<TitleLookupNameEntity> lookupNames, string? tsQuery)
    {
        if (tsQuery is null)
            return titles.Where(_ => false);

        var aliasMatches = lookupNames.Where(name => name.SearchVector.Matches(
            EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, tsQuery))).Select(name => name.TitleId);
        var displayMatches = titles.Where(title => title.SearchVector.Matches(
            EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, tsQuery))).Select(title => title.Id);
        var matchingIds = displayMatches.Union(aliasMatches);
        return titles.Where(title => matchingIds.Contains(title.Id));
    }

    public static IQueryable<DatGameEntity> MatchGames(IQueryable<DatGameEntity> games, string text) =>
        ToTsQueryText(text) is { } tsQuery
            ? games.Where(game => game.SearchVector.Matches(
                EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, tsQuery)))
            : games.Where(_ => false);

    public static string FormatRank(float rank) => rank.ToString("R", CultureInfo.InvariantCulture);

    public static bool TryParseRank(string? value, out float rank) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rank);
}
