using Romd.Domain.Catalog;
using Romd.Application.Common.Systems;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.PlayLater;
using Romd.Domain.Libraries;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Entities;
using Romd.Persistence.Search;
using Romd.Persistence.Queries;
using Romd.Persistence.Systems;

namespace Romd.Persistence.Repositories;

public sealed partial class ConsumerBrowseRepository(
    RomdDbContext context,
    IConsumerReleaseSelector releaseSelector) : IConsumerBrowseRepository, IPlayLaterRepository
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
            (libraryId, token) => ListPlatformsForLibraryAsync(libraryId, scope.UserId, cursor, limit, token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<PagedList<ConsumerPlatformSummaryData>>> ListPlatformsForLibraryAsync(
        int libraryId,
        Guid userId,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        string fingerprint = KeysetCursor.Fingerprint("consumer.library-systems", userId, libraryId);
        var decoded = KeysetCursor.Decode<ConsumerNameCursor>(cursor, fingerprint, value => value.Id > 0 && value.Name is not null);
        if (decoded.IsError)
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerPlatformSummaryData>>.Rejected(decoded.FirstError);
        var cursorData = decoded.Value;

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
                    .FirstOrDefault(),
                ConsoleArtwork = context.ReferenceAssets
                    .Where(asset => asset.Hash == (platform.ConsoleArtworkOverrideHash ?? platform.BaseConsoleArtworkHash))
                    .Select(asset => new ReferenceAssetData("/api/assets/" + asset.Hash, asset.Hash, asset.ContentType, false))
                    .FirstOrDefault(),
                Icon = context.ReferenceAssets
                    .Where(asset => asset.Hash == (platform.HasArtworkOverride ? platform.ArtworkOverrideHash : platform.BaseAssetHash))
                    .Select(asset => new ReferenceAssetData("/api/assets/" + asset.Hash, asset.Hash, asset.ContentType,
                        platform.MonochromeOverride ?? platform.BaseMonochrome))
                    .FirstOrDefault()
            })
            .Take(limit + 1)
            .ToListAsync(ct);

        var manufacturers = await new Romd.Persistence.ReferenceData.SystemCompanyReader(context).ReadAsync(items.Select(x => x.Id), ct);
        items = items.Select(x => x with { Manufacturer = Romd.Persistence.ReferenceData.SystemCompanyReader.Names(manufacturers.GetValueOrDefault(x.Id)) }).ToList();
        return new ConsumerLibraryProjectionResult<PagedList<ConsumerPlatformSummaryData>>.Found(
            ToPage(items, limit, item => new ConsumerNameCursor(item.Name, item.Id), fingerprint));
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
                Media = Array.Empty<ConsumerMediaData>(),
                Description = candidate.HasDescriptionOverride ? candidate.DescriptionOverride : candidate.BaseDescription,
                Hardware = candidate.BuiltInVersion != null
                    ? BuiltInSystemHardware.Get(candidate.CanonicalKey!)
                    : candidate.HardwareCategory == null && candidate.HardwareGeneration == null && candidate.HardwareReleaseYear == null
                        ? null : new SystemHardware(candidate.HardwareCategory, candidate.HardwareGeneration, candidate.HardwareReleaseYear),
                ConsoleArtwork = context.ReferenceAssets
                    .Where(asset => asset.Hash == (candidate.ConsoleArtworkOverrideHash ?? candidate.BaseConsoleArtworkHash))
                    .Select(asset => new ReferenceAssetData("/api/assets/" + asset.Hash, asset.Hash, asset.ContentType, false))
                    .FirstOrDefault(),
                ControllerArtwork = context.ReferenceAssets
                    .Where(asset => asset.Hash == (candidate.ControllerArtworkOverrideHash ?? candidate.BaseControllerArtworkHash))
                    .Select(asset => new ReferenceAssetData("/api/assets/" + asset.Hash, asset.Hash, asset.ContentType, false))
                    .FirstOrDefault(),
                Icon = context.ReferenceAssets
                    .Where(asset => asset.Hash == (candidate.HasArtworkOverride ? candidate.ArtworkOverrideHash : candidate.BaseAssetHash))
                    .Select(asset => new ReferenceAssetData("/api/assets/" + asset.Hash, asset.Hash, asset.ContentType,
                        candidate.MonochromeOverride ?? candidate.BaseMonochrome))
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (platform is null)
        {
            return new ConsumerLibraryProjectionResult<ConsumerPlatformDetailData>.ItemNotFound();
        }

        var media = await context.TitleMedia
            .AsNoTracking()
            .Where(candidate => MediaTypes.ConsumerVisibleNames.Contains(candidate.Type) &&
                OwnedMaterializedLibraryTitles(libraryId).Any(item =>
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
            (libraryId, token) => SearchCatalogForLibraryAsync(
                        libraryId,
                        scope.UserId,
                        filters,
                        releasePreference,
                        cursor,
                        limit,
                        token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>> SearchCatalogForLibraryAsync(
        int libraryId,
        Guid userId,
        ConsumerCatalogFilters filters,
        ConsumerReleasePreference releasePreference,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        string fingerprint = KeysetCursor.Fingerprint("consumer.catalog", userId, libraryId,
            filters.Query?.Trim(), filters.PlatformId, filters.Genre, filters.Completeness, filters.SortField);
        var decoded = KeysetCursor.Decode<ConsumerTitleCursor>(cursor, fingerprint,
            value => value.Id > 0 && value.Name is not null && value.SortField == filters.SortField
                && (value.Rating is null || double.IsFinite(value.Rating.Value)));
        if (decoded.IsError)
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Rejected(decoded.FirstError);
        var cursorData = decoded.Value;

        var query = BuildTitleQuery(libraryId, filters);
        query = ApplyCompletenessFilter(query, libraryId, filters.Completeness);
        query = ApplyTitleCursor(query, cursorData, filters.SortField);
        query = ApplyTitleOrdering(query, filters.SortField);

        var titleRows = await ProjectToTitleCards(query)
            .Take(limit + 1)
            .ToListAsync(ct);

        var items = await CompleteTitleCardsAsync(titleRows, libraryId, releasePreference, ct);
        return new ConsumerLibraryProjectionResult<PagedList<ConsumerTitleCardData>>.Found(ToPage(
            items, limit, item => new ConsumerTitleCursor(filters.SortField, item.Name, item.Rating, item.Id), fingerprint));
    }

    private async Task<List<ConsumerTitleCardData>> CompleteTitleCardsAsync(
        IReadOnlyList<ConsumerTitleCardData> titleRows, int libraryId, ConsumerReleasePreference releasePreference,
        CancellationToken ct)
    {
        if (titleRows.Count == 0)
            return [];

        var titleIds = titleRows.Select(item => item.Id).ToArray();
        var releaseCandidatesByTitleId = await ConsumerReleaseProjection.LoadByTitleIdsAsync(context, libraryId, titleIds, ct);
        var artwork = await new ArtworkReader(context).ResolveAsync(titleIds, ct);
        var systems = await new SystemSummaryReader(context).ReadAsync(titleRows.Select(item => item.PlatformId), ct);
        var ratings = await new ContentRatingSummaryReader(context).ReadAsync(titleRows.SelectMany(item => item.ContentRatings), ct);

        return titleRows.Select(item => AddReleaseSummary(item, releaseCandidatesByTitleId, releasePreference) with
        {
            Artwork = artwork[item.Id],
            System = systems[item.PlatformId],
            ContentRatings = item.ContentRatings.Select(rating => ratings[ContentRatingSummaryReader.Key(rating)]).ToArray()
        }).ToList();
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
                scope.UserId,
                titleId,
                releasePreference,
                token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerTitleDetailData>> GetTitleForLibraryAsync(
        int libraryId,
        Guid userId,
        int titleId,
        ConsumerReleasePreference releasePreference,
        CancellationToken ct)
    {
        var presentedRatings = context.PresentedRatings();
        var title = await context.PresentedTitles()
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
                    ContentRatings = presentedRatings.Where(rating => rating.TitleId == title.Id).OrderBy(rating => rating.Board)
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

        var media = (ConsumerLibraryProjectionResult<PagedList<ConsumerMediaData>>.Found)
            await ReadTitleMediaPageForLibraryAsync(userId, libraryId, titleId, null, 50, ct);
        var releasePage = (ConsumerLibraryProjectionResult<PagedList<ConsumerReleaseData>>.Found)
            await ReadTitleReleasesPageForLibraryAsync(userId, libraryId, titleId, null, 50, ct);

        var releaseCandidatesByTitleId = await ConsumerReleaseProjection.LoadByTitleIdsAsync(
            context,
            libraryId,
            [titleId],
            ct);
        var releaseCandidates = releaseCandidatesByTitleId.GetValueOrDefault(titleId, []);
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
                Media = media.Value.Items,
                MediaNextCursor = media.Value.NextCursor,
                MediaHasNextPage = media.Value.HasNextPage,
                Releases = releasePage.Value.Items,
                ReleasesNextCursor = releasePage.Value.NextCursor,
                ReleasesHasNextPage = releasePage.Value.HasNextPage,
                DefaultReleaseId = defaultRelease?.Id
            });
    }

    private IQueryable<TitleEntity> BuildTitleQuery(int libraryId, ConsumerCatalogFilters filters)
    {
        // Display titles and DAT release names share the indexed phrase and prefix match.
        var query = context.PresentedTitles();
        if (!string.IsNullOrWhiteSpace(filters.Query))
            query = MatchTitleOrReleaseName(query, libraryId, filters.Query);

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

    private IQueryable<TitleEntity> MatchTitleOrReleaseName(IQueryable<TitleEntity> titles, int libraryId, string text)
    {
        var titleMatches = SearchQuery.MatchTitles(titles, context.TitleLookupNames, text).Select(title => title.Id);
        var releaseNameMatches =
            from game in SearchQuery.MatchGames(context.DatGames.Where(game => !game.IsBios), text)
            join dat in context.DatFiles on game.DatFileId equals dat.Id
            join datSource in context.DatSources on dat.DatSourceId equals datSource.Id
            join source in context.CatalogSources on datSource.CatalogSourceId equals source.Id
            join claim in context.CatalogReleaseSources on game.SourceEntryId equals claim.SourceEntryId
            join release in context.MaterializedLibraryReleases on claim.CatalogReleaseId equals release.CatalogReleaseId
            where dat.Lifecycle == "Active" && source.Status == "Active"
                  && release.LibraryId == libraryId && release.IsOwned && release.IsExposed
            select release.TitleId;

        var matchingTitleIds = titleMatches.Union(releaseNameMatches);
        return titles.Where(title => matchingTitleIds.Contains(title.Id));
    }

    private IQueryable<ConsumerTitleCardData> ProjectToTitleCards(IQueryable<TitleEntity> query)
    {
        var presentedRatings = context.PresentedRatings();
        return query.Join(
            context.Platforms.AsNoTracking(),
            title => title.PlatformId,
            platform => platform.Id,
            (title, platform) => new ConsumerTitleCardData
            {
                Id = title.Id,
                PlatformId = title.PlatformId,
                System = new SystemSummaryData(platform.CanonicalKey ?? platform.ShortName, platform.Name, platform.Name, null),
                ContentRatings = presentedRatings.Where(rating => rating.TitleId == title.Id).OrderBy(rating => rating.Board)
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
                EsrbRating = presentedRatings
                    .Where(rating => rating.TitleId == title.Id && rating.Board == (int)Romd.Domain.Catalog.Ratings.RatingBoard.Esrb)
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
    }

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
        Func<T, TCursor> createCursor, string fingerprint) where TCursor : class
    {
        string? nextCursor = null;
        bool hasNextPage = items.Count > limit;

        if (hasNextPage)
        {
            T lastItem = items[limit - 1];
            nextCursor = KeysetCursor.Encode(createCursor(lastItem), fingerprint);
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
