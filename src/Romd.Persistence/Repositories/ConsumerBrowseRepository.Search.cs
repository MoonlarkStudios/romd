using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles.Matching;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Search;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Search;

namespace Romd.Persistence.Repositories;

public sealed partial class ConsumerBrowseRepository
{
    private const int PrefixMatch = 1;
    private const int PhraseMatch = 2;
    private const int ExactAlternateMatch = 3;
    private const int ExactTitleMatch = 4;

    private async Task<ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>> SearchRankedTitlesAsync(
        IQueryable<TitleEntity> titles,
        string? query,
        int libraryId,
        ConsumerReleasePreference releasePreference,
        ConsumerTitleCursor? cursor,
        string fingerprint,
        int limit,
        CancellationToken ct)
    {
        if (query is null || SearchQuery.ToTsQueryText(query) is not { } prefix
            || SearchQuery.ToTsQueryText(query, prefixLastToken: false) is not { } phrase)
        {
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Found(new([], null, false));
        }

        string normalized = SearchTextNormalizer.Normalize(query);
        string titleIdentity = TitleNormalizer.Normalize(query);
        string literal = query.Trim().ToLowerInvariant();
        var displayMatches = context.Titles
            .Where(title => title.SearchVector.Matches(EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, prefix)))
            .Select(title => new CatalogNameMatch
            {
                TitleId = title.Id,
                Name = null,
                Rank = title.SearchDocument == normalized || title.Name.ToLower() == literal || title.NormalizedName == titleIdentity
                    ? ExactTitleMatch
                    : title.SearchVector.Matches(EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, phrase)) ? PhraseMatch : PrefixMatch
            });
        var aliases = context.TitleLookupNames
            .Where(name => name.SearchVector.Matches(EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, prefix)))
            .Select(name => new CatalogNameMatch
            {
                TitleId = name.TitleId,
                Name = name.Name,
                Rank = name.SearchDocument == normalized ? ExactAlternateMatch
                    : name.SearchVector.Matches(EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, phrase)) ? PhraseMatch : PrefixMatch
            });
        var releases = MatchExposedDatGames(libraryId, query).Select(match => new CatalogNameMatch
        {
            TitleId = match.TitleId,
            Name = match.Game.Name,
            Rank = match.Game.SearchDocument == normalized || match.Game.Name.ToLower() == literal ? ExactAlternateMatch
                : match.Game.SearchVector.Matches(EF.Functions.ToTsQuery(SearchDocuments.TextSearchConfiguration, phrase)) ? PhraseMatch : PrefixMatch
        });
        var alternateMatches = aliases.Concat(releases);
        var ranks = displayMatches.Concat(alternateMatches)
            .GroupBy(match => match.TitleId)
            .Select(matches => new
            {
                TitleId = matches.Key,
                Rank = matches.Max(match => match.Rank),
                DisplayRank = matches.Max(match => match.Name == null ? match.Rank : 0)
            });

        var results =
            from title in titles
            join match in ranks on title.Id equals match.TitleId
            select new { title.Id, title.Name, match.Rank, match.DisplayRank };
        if (cursor?.Rank is { } lastRank)
        {
            results = results.Where(row => row.Rank < lastRank ||
                (row.Rank == lastRank && (row.Name.CompareTo(cursor.Name) > 0 ||
                    (row.Name == cursor.Name && row.Id > cursor.Id))));
        }

        var rows = await results.OrderByDescending(row => row.Rank)
            .ThenBy(row => row.Name).ThenBy(row => row.Id)
            .Take(limit + 1)
            .ToListAsync(ct);
        var page = ToPage(rows, limit, row =>
            new ConsumerTitleCursor(ConsumerTitleSortField.Relevance, row.Name, null, row.Id, row.Rank), fingerprint);
        if (page.Items.Count == 0)
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Found(new([], null, false));

        var explainedIds = page.Items.Where(row => row.Rank > row.DisplayRank).Select(row => row.Id).ToArray();
        var matchedNames = explainedIds.Length == 0
            ? new Dictionary<int, string?>()
            : await alternateMatches.Where(match => explainedIds.Contains(match.TitleId))
                .GroupBy(match => match.TitleId)
                .Select(matches => new
                {
                    TitleId = matches.Key,
                    Name = matches.OrderByDescending(match => match.Rank).ThenBy(match => match.Name)
                        .Select(match => match.Name).FirstOrDefault()
                })
                .ToDictionaryAsync(match => match.TitleId, match => match.Name, ct);

        // The scoped page and these cards share the same library read snapshot.
        var ids = page.Items.Select(row => row.Id).ToArray();
        var cards = await ProjectToTitleCards(context.PresentedTitles().Where(title => ids.Contains(title.Id)))
            .ToDictionaryAsync(card => card.Id, ct);
        var orderedCards = page.Items
            .Select(row => cards[row.Id] with { MatchedName = matchedNames.GetValueOrDefault(row.Id) }).ToArray();
        var items = await CompleteTitleCardsAsync(orderedCards, libraryId, releasePreference, ct);
        return new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Found(
            new(items, page.NextCursor, page.HasNextPage));
    }

    private sealed class CatalogNameMatch
    {
        public int TitleId { get; init; }
        public string? Name { get; init; }
        public int Rank { get; init; }
    }
}
