using Romd.Application.Common.Pagination;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Extensions;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Read-only title projections. One scoped instance serves the title, enrichment-status, and
///     media read roles; none of them returns a title aggregate.
/// </summary>
public sealed class TitleReadRepository(RomdDbContext context)
    : ITitleReader, ITitleEnrichmentStatusReader, ITitleMediaReader
{
    public async Task<TitleSummary?> GetSummaryAsync(int titleId, CancellationToken cancellationToken = default)
    {
        var row = await ProjectSummaries(context.Titles.AsNoTracking().Where(title => title.Id == titleId))
            .FirstOrDefaultAsync(cancellationToken);
        return row?.ToSummary();
    }

    public Task<Guid?> GetRevisionAsync(int titleId, CancellationToken cancellationToken = default) =>
        context.Titles.Where(title => title.Id == titleId).Select(title => (Guid?)title.Revision)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedList<TitleSummary>> ListByPlatformAsync(
        PlatformTitlePageRequest request,
        CancellationToken cancellationToken = default)
    {
        var titles = context.Titles
            .AsNoTracking()
            .Where(title => title.PlatformId == request.PlatformId)
            .ApplyLibrary(request.LibraryId, context.MaterializedLibraryTitles, context.Libraries);
        if (request.LocalPayloadOnly)
            titles = titles.Where(title => title.HasLocalPayload);
        if (request.AfterName is { } name && request.AfterId is { } id)
            titles = titles.Where(title => string.Compare(title.Name, name) > 0 || (title.Name == name && title.Id > id));

        var rows = await ProjectSummaries(titles.OrderBy(title => title.Name).ThenBy(title => title.Id))
            .Take(request.Limit + 1)
            .ToListAsync(cancellationToken);
        bool hasNext = rows.Count > request.Limit;
        var page = rows.Take(request.Limit).Select(row => row.ToSummary()).ToList();
        string? next = hasNext ? request.CursorFor(page[^1].Name, page[^1].Id) : null;
        return new PagedList<TitleSummary>(page, next, hasNext);
    }

    public async Task<IReadOnlyList<int>> GetIdsByPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default) =>
        await context.Titles
            .AsNoTracking()
            .Where(title => title.PlatformId == platformId)
            .Select(title => title.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EnrichmentCandidate>> GetCandidatesAsync(
        int platformId,
        EnrichmentScope scope,
        CancellationToken cancellationToken = default)
    {
        string noneStatus = EnrichmentStatus.None.ToString();
        string pendingStatus = EnrichmentStatus.Pending.ToString();
        string lowConfidenceStatus = EnrichmentStatus.LowConfidence.ToString();

        var query = context.Titles
            .AsNoTracking()
            .Where(title => title.PlatformId == platformId
                && (title.EnrichmentStatus == noneStatus
                    || title.EnrichmentStatus == pendingStatus
                    || title.EnrichmentStatus == lowConfidenceStatus));

        if (scope == EnrichmentScope.Tracked)
        {
            query = query.Where(title => context.TrackedTitles.Any(tracked => tracked.TitleId == title.Id));
        }

        return await query
            .OrderBy(title => title.Name)
            .Select(title => new EnrichmentCandidate(title.Id, title.Name, title.HasLocalPayload))
            .ToListAsync(cancellationToken);
    }

    public async Task<TitleEnrichmentStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        string pendingStatus = EnrichmentStatus.Pending.ToString();
        string completedStatus = EnrichmentStatus.Completed.ToString();
        string notFoundStatus = EnrichmentStatus.NotFound.ToString();
        string failedStatus = EnrichmentStatus.Failed.ToString();
        string lowConfidenceStatus = EnrichmentStatus.LowConfidence.ToString();

        // Enrichment progress is reported over the curated (tracked) set, mirroring the scope
        // that bulk enrichment targets by default.
        var stats = await context.Titles
            .Where(title => context.TrackedTitles.Any(tracked => tracked.TitleId == title.Id))
            .GroupBy(_ => 1)
            .Select(group => new TitleEnrichmentStats(
                group.Count(title => title.EnrichmentStatus == pendingStatus),
                group.Count(title => title.EnrichmentStatus == completedStatus),
                group.Count(title => title.EnrichmentStatus == notFoundStatus),
                group.Count(title => title.EnrichmentStatus == failedStatus),
                group.Count(title => title.EnrichmentStatus == lowConfidenceStatus)))
            .FirstOrDefaultAsync(cancellationToken);

        return stats ?? new TitleEnrichmentStats(0, 0, 0, 0, 0);
    }

    public async Task<IReadOnlyList<TitleMediaSummary>> ListAsync(int titleId, CancellationToken cancellationToken = default)
    {
        var rows = await context.TitleMedia
            .AsNoTracking()
            .Where(media => media.TitleId == titleId)
            .OrderBy(media => media.Id)
            .Select(media => new
            {
                media.Id,
                media.Type,
                media.FileId,
                media.SourceId,
                media.SourceUrl,
                media.Attribution,
                media.SourcePageUrl,
                media.IsPrimary
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new TitleMediaSummary(
                row.Id,
                Enum.TryParse<MediaType>(row.Type, out var type) ? type : MediaType.Cover,
                row.FileId,
                row.SourceId,
                row.SourceUrl,
                row.Attribution,
                row.SourcePageUrl,
                row.IsPrimary))
            .ToList();
    }

    /// <summary>
    ///     The single title-summary projection. <c>HasLocalPayload</c> is the materialized
    ///     projection-owned fact; it is never re-derived from provider payload tables here.
    /// </summary>
    private static IQueryable<SummaryRow> ProjectSummaries(IQueryable<TitleEntity> titles) =>
        titles.Select(title => new SummaryRow(
            title.Id,
            title.PlatformId,
            title.Name,
            title.EnrichmentStatus,
            title.Description,
            title.Publisher,
            title.Developer,
            title.Genre,
            title.ReleaseDate,
            title.Players,
            title.Rating,
            title.CreatedAt,
            title.LastEnrichedAt,
            title.HasLocalPayload));

    private sealed record SummaryRow(
        int Id,
        int PlatformId,
        string Name,
        string Status,
        string? Description,
        string? Publisher,
        string? Developer,
        string? Genre,
        DateOnly? ReleaseDate,
        int? Players,
        double? Rating,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastEnrichedAt,
        bool HasLocalPayload)
    {
        public TitleSummary ToSummary() => new(
            Id,
            PlatformId,
            Name,
            Enum.TryParse<EnrichmentStatus>(Status, out var status) ? status : EnrichmentStatus.None,
            Description,
            Publisher,
            Developer,
            Genre,
            ReleaseDate,
            Players,
            Rating,
            CreatedAt,
            LastEnrichedAt,
            HasLocalPayload);
    }
}
