using Romd.Application.Common.Systems;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;
using Romd.Persistence.Search;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerBrowseRepository(
    RomdDbContext context,
    IConsumerReleaseSelector releaseSelector) : IConsumerBrowseRepository
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();
    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);

    public Task<ConsumerLibraryReadResult<PagedList<ConsumerPlatformSummaryData>>> ListPlatformsAsync(
        ConsumerLibraryScope scope,
        string? cursor,
        int limit,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<PagedList<ConsumerPlatformSummaryData>>(
            scope,
            async (libraryId, token) =>
                new ConsumerLibraryProjectionResult<PagedList<ConsumerPlatformSummaryData>>.Found(
                    await ListPlatformsForLibraryAsync(libraryId, cursor, limit, token)),
            ct);

    private async Task<PagedList<ConsumerPlatformSummaryData>> ListPlatformsForLibraryAsync(
        int libraryId,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        var cursorData = CursorUtils.FromCursor<ConsumerNameCursor>(cursor);

        var query = context.Platforms
            .AsNoTracking()
            .Where(platform => OwnedMaterializedLibraryTitles(libraryId).Any(item => item.PlatformId == platform.Id));

        if (cursorData is not null)
        {
            query = query.Where(platform =>
                platform.Name.CompareTo(cursorData.Name) > 0 ||
                (platform.Name == cursorData.Name && platform.Id > cursorData.Id));
        }

        var items = await query
            .OrderBy(platform => platform.Name)
            .ThenBy(platform => platform.Id)
            .Select(platform => new ConsumerPlatformSummaryData
            {
                Key = platform.CanonicalKey ?? platform.ShortName,
                Id = platform.Id,
                Name = platform.Name,
                ShortName = platform.ShortName,
                TitleCount = OwnedMaterializedLibraryTitles(libraryId)
                    .Where(item => item.PlatformId == platform.Id)
                    .Count(),
                CoverMediaId = context.TitleMedia
                    .Where(media =>
                        media.Type == "Cover" &&
                        OwnedMaterializedLibraryTitles(libraryId).Any(item =>
                            item.PlatformId == platform.Id &&
                            item.TitleId == media.TitleId))
                    .OrderByDescending(media => media.IsPrimary)
                    .ThenBy(media => media.Id)
                    .Select(media => (int?)media.Id)
                    .FirstOrDefault()
            })
            .Take(limit + 1)
            .ToListAsync(ct);

        var manufacturers = await new Romd.Persistence.ReferenceData.SystemCompanyReader(context).ReadAsync(items.Select(x => x.Id), ct);
        items = items.Select(x => x with { Manufacturer = Romd.Persistence.ReferenceData.SystemCompanyReader.Names(manufacturers.GetValueOrDefault(x.Id)) }).ToList();
        return ToPage(items, limit, item => new ConsumerNameCursor(item.Name, item.Id));
    }

    public Task<ConsumerLibraryReadResult<ConsumerPlatformDetailData>> GetPlatformAsync(
        ConsumerLibraryScope scope,
        int platformId,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<ConsumerPlatformDetailData>(
            scope,
            (libraryId, token) => GetPlatformForLibraryAsync(libraryId, platformId, token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerPlatformDetailData>> GetPlatformForLibraryAsync(
        int libraryId,
        int platformId,
        CancellationToken ct)
    {
        var platform = await context.Platforms
            .AsNoTracking()
            .Where(candidate => candidate.Id == platformId)
            .Where(candidate => OwnedMaterializedLibraryTitles(libraryId).Any(item => item.PlatformId == candidate.Id))
            .Select(candidate => new ConsumerPlatformDetailData
            {
                Key = candidate.CanonicalKey ?? candidate.ShortName,
                Id = candidate.Id,
                Name = candidate.Name,
                ShortName = candidate.ShortName,
                TitleCount = OwnedMaterializedLibraryTitles(libraryId)
                    .Where(item => item.PlatformId == candidate.Id)
                    .Count(),
                Media = Array.Empty<ConsumerMediaData>()
            })
            .FirstOrDefaultAsync(ct);

        if (platform is null)
        {
            return new ConsumerLibraryProjectionResult<ConsumerPlatformDetailData>.ItemNotFound();
        }

        var media = await context.TitleMedia
            .AsNoTracking()
            .Where(candidate => OwnedMaterializedLibraryTitles(libraryId).Any(item =>
                item.PlatformId == platformId &&
                item.TitleId == candidate.TitleId))
            .OrderByDescending(candidate => candidate.IsPrimary)
            .ThenBy(candidate => candidate.Type)
            .ThenBy(candidate => candidate.Id)
            .Take(12)
            .Select(candidate => new ConsumerMediaData
            {
                Id = candidate.Id,
                Type = candidate.Type,
                IsPrimary = candidate.IsPrimary
            })
            .ToListAsync(ct);

        var manufacturers = await new Romd.Persistence.ReferenceData.SystemCompanyReader(context).ReadAsync([platformId], ct);
        return new ConsumerLibraryProjectionResult<ConsumerPlatformDetailData>.Found(
            platform with { Media = media, Manufacturer = Romd.Persistence.ReferenceData.SystemCompanyReader.Names(manufacturers.GetValueOrDefault(platformId)) });
    }

    public Task<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>> SearchCatalogAsync(
        ConsumerLibraryScope scope,
        ConsumerCatalogFilters filters,
        ConsumerReleasePreference releasePreference,
        string? cursor,
        int limit,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<PagedList<ConsumerTitleCardData>>(
            scope,
            async (libraryId, token) =>
                new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Found(
                    await SearchCatalogForLibraryAsync(
                        libraryId,
                        filters,
                        releasePreference,
                        cursor,
                        limit,
                        token)),
            ct);

    private async Task<PagedList<ConsumerTitleCardData>> SearchCatalogForLibraryAsync(
        int libraryId,
        ConsumerCatalogFilters filters,
        ConsumerReleasePreference releasePreference,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        var cursorData = CursorUtils.FromCursor<ConsumerTitleCursor>(cursor);
        if (cursorData is not null && cursorData.SortField != filters.SortField)
        {
            cursorData = null;
        }

        var query = BuildTitleQuery(libraryId, filters);
        query = ApplyCompletenessFilter(query, libraryId, filters.Completeness);
        query = ApplyTitleCursor(query, cursorData, filters.SortField);
        query = ApplyTitleOrdering(query, filters.SortField);

        var titleRows = await ProjectToTitleCards(query)
            .Take(limit + 1)
            .ToListAsync(ct);

        var releaseCandidatesByTitleId = await ConsumerReleaseProjection.LoadByTitleIdsAsync(
            context,
            libraryId,
            titleRows.Select(item => item.Id).ToList(),
            ct);

        var artwork = await new ArtworkReader(context).ResolveAsync(titleRows.Select(t => t.Id).ToArray(), ct);

        var systems = await new SystemSummaryReader(context).ReadAsync(titleRows.Select(x => x.PlatformId), ct);

        var ratings = await new ContentRatingSummaryReader(context).ReadAsync(titleRows.SelectMany(x => x.ContentRatings), ct);

        var items = titleRows
            .Select(item => AddReleaseSummary(item, releaseCandidatesByTitleId, releasePreference) with { Artwork = artwork[item.Id], System = systems[item.PlatformId], ContentRatings = item.ContentRatings.Select(rating => ratings[ContentRatingSummaryReader.Key(rating)]).ToArray() })
            .ToList();

        return ToPage(
            items,
            limit,
            item => new ConsumerTitleCursor(filters.SortField, item.Name, item.Rating, item.Id));
    }

    public Task<ConsumerLibraryReadResult<ConsumerTitleDetailData>> GetTitleAsync(
        ConsumerLibraryScope scope,
        int titleId,
        ConsumerReleasePreference releasePreference,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<ConsumerTitleDetailData>(
            scope,
            (libraryId, token) => GetTitleForLibraryAsync(
                libraryId,
                titleId,
                releasePreference,
                token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerTitleDetailData>> GetTitleForLibraryAsync(
        int libraryId,
        int titleId,
        ConsumerReleasePreference releasePreference,
        CancellationToken ct)
    {
        var title = await context.Titles
            .AsNoTracking()
            .Where(candidate => candidate.Id == titleId)
            .Where(candidate => OwnedMaterializedLibraryTitles(libraryId).Any(item => item.TitleId == candidate.Id))
            .Join(
                context.Platforms,
                title => title.PlatformId,
                platform => platform.Id,
                (title, platform) => new ConsumerTitleDetailData
                {
                    Id = title.Id,
                    PlatformId = title.PlatformId,
                    System = new SystemSummaryData(platform.CanonicalKey ?? platform.ShortName, platform.Name, platform.Name, null),
                    ContentRatings = title.ContentRatings.OrderBy(rating => rating.Board)
                        .Select(rating => new ConsumerContentRatingData
                        {
                            Board = (Romd.Domain.Catalog.Ratings.RatingBoard)rating.Board,
                            Code = rating.Code
                        }).ToList(),
                    Name = title.Name,
                    Description = title.Description,
                    Publisher = title.Publisher,
                    Developer = title.Developer,
                    Genre = title.Genre,
                    ReleaseDate = title.ReleaseDate,
                    Players = title.Players,
                    Rating = title.Rating,
                    Media = Array.Empty<ConsumerMediaData>(),
                    Releases = Array.Empty<ConsumerReleaseData>(),
                    DefaultReleaseId = null
                })
            .FirstOrDefaultAsync(ct);

        if (title is null)
        {
            return new ConsumerLibraryProjectionResult<ConsumerTitleDetailData>.ItemNotFound();
        }

        var media = await context.TitleMedia
            .AsNoTracking()
            .Where(candidate => candidate.TitleId == titleId)
            .OrderBy(candidate => candidate.Type)
            .ThenByDescending(candidate => candidate.IsPrimary)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new ConsumerMediaData
            {
                Id = candidate.Id,
                Type = candidate.Type,
                IsPrimary = candidate.IsPrimary
            })
            .ToListAsync(ct);

        var releaseCandidatesByTitleId = await ConsumerReleaseProjection.LoadByTitleIdsAsync(
            context,
            libraryId,
            [titleId],
            ct);
        var releaseCandidates = releaseCandidatesByTitleId.GetValueOrDefault(titleId, []);
        var releases = releaseCandidates
            .Select(candidate => candidate.Release)
            .ToList();
        var defaultRelease = releaseSelector.SelectDefault(releaseCandidates, releasePreference);

        var artwork = await new ArtworkReader(context).ResolveAsync([titleId], ct);
        var systems = await new SystemSummaryReader(context).ReadAsync([title.PlatformId], ct);

        var ratings = await new ContentRatingSummaryReader(context).ReadAsync(title.ContentRatings, ct);

        return new ConsumerLibraryProjectionResult<ConsumerTitleDetailData>.Found(
            title with
            {
                Artwork = artwork[titleId],
                System = systems[title.PlatformId],
                ContentRatings = title.ContentRatings.Select(rating => ratings[ContentRatingSummaryReader.Key(rating)]).ToArray(),
                Media = media,
                Releases = releases,
                DefaultReleaseId = defaultRelease?.Id
            });
    }

    private IQueryable<TitleEntity> BuildTitleQuery(int libraryId, ConsumerCatalogFilters filters)
    {
        // Reuse the admin tsvector match so the consumer query is sargable against the GIN index
        // rather than a non-indexable LIKE '%q%'. Token/prefix match semantics differ from
        // substring matching (same behaviour admin search already uses).
        var query = string.IsNullOrWhiteSpace(filters.Query)
            ? context.Titles.AsNoTracking()
            : SearchQuery.MatchTitles(context.Titles.AsNoTracking(), filters.Query);

        query = query.Where(title =>
            OwnedMaterializedLibraryTitles(libraryId).Any(item => item.TitleId == title.Id));

        if (filters.PlatformId is { } platformId)
        {
            query = query.Where(title => title.PlatformId == platformId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Genre))
        {
            query = query.Where(title => title.Genre == filters.Genre);
        }

        return query;
    }

    private IQueryable<ConsumerTitleCardData> ProjectToTitleCards(IQueryable<TitleEntity> query) =>
        query.Join(
            context.Platforms.AsNoTracking(),
            title => title.PlatformId,
            platform => platform.Id,
            (title, platform) => new ConsumerTitleCardData
            {
                Id = title.Id,
                PlatformId = title.PlatformId,
                System = new SystemSummaryData(platform.CanonicalKey ?? platform.ShortName, platform.Name, platform.Name, null),
                ContentRatings = title.ContentRatings.OrderBy(rating => rating.Board)
                    .Select(rating => new ConsumerContentRatingData
                    {
                        Board = (Romd.Domain.Catalog.Ratings.RatingBoard)rating.Board,
                        Code = rating.Code
                    }).ToList(),
                Name = title.Name,
                Genre = title.Genre,
                ReleaseDate = title.ReleaseDate,
                Rating = title.Rating,
                Players = title.Players,
                EsrbRating = title.ContentRatings
                    .Where(rating => rating.Board == (int)Romd.Domain.Catalog.Ratings.RatingBoard.Esrb)
                    .Select(rating => rating.Code)
                    .FirstOrDefault(),
                CoverMediaId = context.TitleMedia
                    .Where(media => media.TitleId == title.Id && media.Type == "Cover")
                    .OrderByDescending(media => media.IsPrimary)
                    .ThenBy(media => media.Id)
                    .Select(media => (int?)media.Id)
                    .FirstOrDefault(),
                ReleaseCount = 0,
                DefaultReleaseId = null
            });

    private ConsumerTitleCardData AddReleaseSummary(
        ConsumerTitleCardData item,
        IReadOnlyDictionary<int, IReadOnlyList<ConsumerReleaseSelectionCandidate>> releaseCandidatesByTitleId,
        ConsumerReleasePreference releasePreference)
    {
        var releaseCandidates = releaseCandidatesByTitleId.GetValueOrDefault(item.Id, []);
        var releases = releaseCandidates
            .Select(candidate => candidate.Release)
            .ToList();
        var defaultRelease = releaseSelector.SelectDefault(releaseCandidates, releasePreference);

        return item with
        {
            ReleaseCount = releases.Count,
            DefaultReleaseId = defaultRelease?.Id
        };
    }

    private IQueryable<TitleEntity> ApplyCompletenessFilter(
        IQueryable<TitleEntity> query,
        int libraryId,
        ConsumerCompletenessFilter completeness) =>
        completeness switch
        {
            ConsumerCompletenessFilter.Complete => query.Where(title =>
                OwnedExposedMaterializedLibraryReleases(libraryId)
                    .Any(release => release.TitleId == title.Id) &&
                OwnedExposedMaterializedLibraryReleases(libraryId)
                    .Where(release => release.TitleId == title.Id)
                    .All(release => release.IsComplete)),
            ConsumerCompletenessFilter.Partial => query.Where(title =>
                OwnedExposedMaterializedLibraryReleases(libraryId)
                    .Any(release => release.TitleId == title.Id && !release.IsComplete)),
            _ => query
        };

    private static IQueryable<TitleEntity> ApplyTitleCursor(
        IQueryable<TitleEntity> query,
        ConsumerTitleCursor? cursor,
        ConsumerTitleSortField sortField)
    {
        if (cursor is null)
        {
            return query;
        }

        return sortField switch
        {
            ConsumerTitleSortField.Rating => ApplyRatingCursor(query, cursor),
            _ => query.Where(title =>
                title.Name.CompareTo(cursor.Name) > 0 ||
                (title.Name == cursor.Name && title.Id > cursor.Id))
        };
    }

    private static IQueryable<TitleEntity> ApplyRatingCursor(
        IQueryable<TitleEntity> query,
        ConsumerTitleCursor cursor)
    {
        if (cursor.Rating is null)
        {
            return query.Where(title =>
                title.Rating == null &&
                (title.Name.CompareTo(cursor.Name) > 0 ||
                 (title.Name == cursor.Name && title.Id > cursor.Id)));
        }

        return query.Where(title =>
            (title.Rating != null && title.Rating < cursor.Rating) ||
            (title.Rating == cursor.Rating &&
             (title.Name.CompareTo(cursor.Name) > 0 ||
              (title.Name == cursor.Name && title.Id > cursor.Id))) ||
            title.Rating == null);
    }

    private static IQueryable<TitleEntity> ApplyTitleOrdering(
        IQueryable<TitleEntity> query,
        ConsumerTitleSortField sortField) =>
        sortField switch
        {
            ConsumerTitleSortField.Rating => query
                .OrderByDescending(title => title.Rating != null)
                .ThenByDescending(title => title.Rating)
                .ThenBy(title => title.Name)
                .ThenBy(title => title.Id),
            _ => query
                .OrderBy(title => title.Name)
                .ThenBy(title => title.Id)
        };

    private IQueryable<MaterializedLibraryTitleEntity> OwnedMaterializedLibraryTitles(int libraryId) =>
        context.MaterializedLibraryTitles
            .AsNoTracking()
            .Where(title =>
                title.LibraryId == libraryId &&
                title.IsOwned &&
                context.Libraries.Any(library =>
                    library.Id == title.LibraryId &&
                    library.ConfigurationState == ValidConfigurationState));

    private IQueryable<MaterializedLibraryReleaseEntity> OwnedExposedMaterializedLibraryReleases(int libraryId) =>
        context.MaterializedLibraryReleases
            .AsNoTracking()
            .Where(release =>
                release.LibraryId == libraryId &&
                release.IsOwned &&
                release.IsExposed &&
                context.Libraries.Any(library =>
                    library.Id == release.LibraryId &&
                    library.ConfigurationState == ValidConfigurationState));

    private static PagedList<T> ToPage<T, TCursor>(
        List<T> items,
        int limit,
        Func<T, TCursor> createCursor)
    {
        string? nextCursor = null;
        bool hasNextPage = items.Count > limit;

        if (hasNextPage)
        {
            T lastItem = items[limit - 1];
            nextCursor = CursorUtils.ToCursor(createCursor(lastItem));
            items = items.Take(limit).ToList();
        }

        return new PagedList<T>(items, nextCursor, hasNextPage);
    }

    private sealed record ConsumerNameCursor(string Name, int Id);

    private sealed record ConsumerTitleCursor(
        ConsumerTitleSortField SortField,
        string Name,
        double? Rating,
        int Id);
}
