using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Persistence.Queries;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Source;

/// <summary>
///     Catalog-owned materialization of local payload availability. Work is chunked to stay
///     below provider parameter limits and to bound memory at million-title scale.
/// </summary>
public sealed class TitlePayloadAvailabilityProjection(
    RomdDbContext context,
    ICatalogPayloadAssertionReader assertions,
    ICatalogPayloadAssertionSynchronizer? assertionSynchronizer = null) : ITitlePayloadAvailabilityProjection
{
    internal const int BatchSize = 5_000;

    public async Task RefreshPayloadAssertionsAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();

        foreach (var titleBatch in titleIds.Where(id => id > 0).Distinct().Chunk(BatchSize))
        {
            var ids = titleBatch.ToList();
            await RefreshAssertionsForTitlesAsync(ids, cancellationToken);
            await RollupAsync(
                context.Titles.Where(IntegerKeyPredicate.Create<TitleEntity>(ids, title => title.Id)),
                cancellationToken);
        }
    }

    public async Task RefreshSourceEntryPayloadAssertionsAsync(
        IReadOnlyCollection<int> sourceEntryIds,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();

        foreach (var entryBatch in sourceEntryIds.Where(id => id > 0).Distinct().Chunk(BatchSize))
        {
            var ids = entryBatch.ToList();
            await RefreshAssertionsAsync(ids, cancellationToken);
            var linkedTitleIds = context.TitleSourceLinks
                .Where(IntegerKeyPredicate.Create<TitleSourceLinkEntity>(ids, link => link.SourceEntryId))
                .Select(link => link.TitleId);
            await RollupAsync(
                context.Titles.Where(title => linkedTitleIds.Contains(title.Id)),
                cancellationToken);
        }
    }

    public async Task RefreshCatalogSourcePayloadAssertionsAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        if (assertionSynchronizer is not null)
        {
            await assertionSynchronizer.SynchronizeCatalogSourceAsync(catalogSourceId, cancellationToken);
            await RollupCatalogSourceAsync(catalogSourceId, cancellationToken);
            return;
        }

        int afterEntryId = 0;
        while (true)
        {
            var entryIds = await context.SourceEntries
                .AsNoTracking()
                .Where(entry => entry.CatalogSourceId == catalogSourceId && entry.Id > afterEntryId)
                .OrderBy(entry => entry.Id)
                .Select(entry => entry.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (entryIds.Count == 0)
            {
                break;
            }

            await RefreshAssertionsAsync(entryIds, cancellationToken);
            afterEntryId = entryIds[^1];
        }

        await RollupCatalogSourceAsync(catalogSourceId, cancellationToken);
    }

    public async Task RefreshPlatformPayloadAssertionsAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        if (assertionSynchronizer is not null)
        {
            await assertionSynchronizer.SynchronizePlatformAsync(platformId, cancellationToken);
            await RollupPlatformAsync(platformId, cancellationToken);
            return;
        }

        int afterEntryId = 0;
        while (true)
        {
            var entryIds = await context.SourceEntries
                .AsNoTracking()
                .Where(entry => entry.PlatformId == platformId && entry.Id > afterEntryId)
                .OrderBy(entry => entry.Id)
                .Select(entry => entry.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (entryIds.Count == 0)
            {
                break;
            }

            await RefreshAssertionsAsync(entryIds, cancellationToken);
            afterEntryId = entryIds[^1];
        }

        await RollupPlatformAsync(platformId, cancellationToken);
    }

    public async Task RollupTitlesAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        foreach (var titleBatch in titleIds.Where(id => id > 0).Distinct().Chunk(BatchSize))
        {
            var ids = titleBatch.ToList();
            await RollupAsync(
                context.Titles.Where(IntegerKeyPredicate.Create<TitleEntity>(ids, title => title.Id)),
                cancellationToken);
        }
    }

    public async Task RollupPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        await RollupAsync(context.Titles.Where(title => title.PlatformId == platformId), cancellationToken);
    }

    public async Task RollupTitleRangeAsync(
        int afterTitleId,
        int throughTitleId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        await RollupAsync(
            context.Titles.Where(title => title.Id > afterTitleId && title.Id <= throughTitleId),
            cancellationToken);
    }

    public async Task RollupCatalogSourceAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        var linkedTitleIds =
            from link in context.TitleSourceLinks
            join entry in context.SourceEntries on link.SourceEntryId equals entry.Id
            where entry.CatalogSourceId == catalogSourceId
            select link.TitleId;
        await RollupAsync(
            context.Titles.Where(title => linkedTitleIds.Contains(title.Id)),
            cancellationToken);
    }

    public async Task<PayloadAvailabilityAuditResult> AuditBatchAsync(
        int? afterTitleId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        int boundedLimit = Math.Min(limit, BatchSize);

        var titleIds = await context.Titles
            .AsNoTracking()
            .Where(title => !afterTitleId.HasValue || title.Id > afterTitleId.Value)
            .OrderBy(title => title.Id)
            .Select(title => title.Id)
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);

        if (titleIds.Count > 0)
        {
            await RefreshAssertionsForTitlesAsync(titleIds, cancellationToken);
            int lowerBound = afterTitleId ?? 0;
            int upperBound = titleIds[^1];
            await RollupTitleRangeAsync(lowerBound, upperBound, cancellationToken);
        }
        return new PayloadAvailabilityAuditResult(
            titleIds.Count,
            titleIds.Count == boundedLimit ? titleIds[^1] : null);
    }

    private async Task RefreshAssertionsForTitlesAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken)
    {
        var entryIds = await context.TitleSourceLinks
            .AsNoTracking()
            .Where(IntegerKeyPredicate.Create<TitleSourceLinkEntity>(titleIds, link => link.TitleId))
            .Select(link => link.SourceEntryId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (entryIds.Count == 0)
        {
            return;
        }

        await RefreshAssertionsAsync(entryIds, cancellationToken);
    }

    private async Task RefreshAssertionsAsync(
        IReadOnlyCollection<int> entryIds,
        CancellationToken cancellationToken)
    {
        var providerAssertions = await assertions.ReadAsync(entryIds, cancellationToken);
        var locallyAvailableEntryIds = providerAssertions
            .Where(assertion => assertion.HasLocalPayload)
            .Select(assertion => assertion.SourceEntryId)
            .Distinct()
            .ToList();

        await context.SourceEntries
            .Where(IntegerKeyPredicate.Create<SourceEntryEntity>(entryIds, entry => entry.Id))
            .Where(entry => entry.HasLocalPayload)
            .ExecuteUpdateAsync(
                update => update.SetProperty(entry => entry.HasLocalPayload, false),
                cancellationToken);

        if (locallyAvailableEntryIds.Count > 0)
        {
            await context.SourceEntries
                .Where(IntegerKeyPredicate.Create<SourceEntryEntity>(
                    locallyAvailableEntryIds,
                    entry => entry.Id))
                .Where(entry => !entry.HasLocalPayload)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(entry => entry.HasLocalPayload, true),
                    cancellationToken);
        }
    }

    private async Task RollupAsync(
        IQueryable<TitleEntity> scope,
        CancellationToken cancellationToken)
    {
        var effectiveLocalPayloadEntries = context.EffectiveSourceEntries()
            .Where(entry => entry.HasLocalPayload);
        await scope
            .Where(title => title.HasLocalPayload != context.TitleSourceLinks.Any(link =>
                link.TitleId == title.Id && effectiveLocalPayloadEntries.Any(entry =>
                    entry.Id == link.SourceEntryId)))
            .ExecuteUpdateAsync(
                update => update.SetProperty(
                    title => title.HasLocalPayload,
                    title => context.TitleSourceLinks.Any(link =>
                        link.TitleId == title.Id && effectiveLocalPayloadEntries.Any(entry =>
                            entry.Id == link.SourceEntryId))),
                cancellationToken);
    }

    private void RequireTransaction()
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Payload availability refresh requires a caller-owned transaction.");
        }
    }
}
