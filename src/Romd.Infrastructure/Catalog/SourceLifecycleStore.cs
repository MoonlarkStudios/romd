using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Persistence;

namespace Romd.Infrastructure.Catalog;

public sealed class SourceLifecycleStore(RomdDbContext context) : ISourceLifecycle
{
    public Task PreserveOwnedTitleIdentitiesAsync(int catalogSourceId, CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Identity retention requires the source mutation transaction.");
        return context.Titles.Where(t => !t.RetainWithoutCatalog && context.TitleSourceLinks.Any(l => l.TitleId == t.Id
                && context.SourceEntries.Any(e => e.Id == l.SourceEntryId && e.CatalogSourceId == catalogSourceId && e.HasLocalPayload)))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RetainWithoutCatalog, true), cancellationToken);
    }

    public async Task<CatalogSourceStatus?> GetStatusAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default)
    {
        string? status = await context.CatalogSources
            .AsNoTracking()
            .Where(s => s.Id == catalogSourceId)
            .Select(s => s.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is null ? null : Enum.Parse<CatalogSourceStatus>(status);
    }

    public async Task SetStatusAsync(
        int catalogSourceId,
        CatalogSourceStatus status,
        CancellationToken cancellationToken = default)
    {
        string statusName = status.ToString();
        await context.CatalogSources
            .Where(s => s.Id == catalogSourceId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Status, statusName),
                cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetEntryPlatformIdsAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default)
    {
        return await context.SourceEntries
            .AsNoTracking()
            .Where(e => e.CatalogSourceId == catalogSourceId && e.PlatformId != null)
            .Select(e => e.PlatformId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetLinkedTitleIdsAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default)
    {
        return await context.TitleSourceLinks
            .AsNoTracking()
            .Where(l => context.SourceEntries.Any(
                e => e.Id == l.SourceEntryId && e.CatalogSourceId == catalogSourceId))
            .Select(l => l.TitleId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetOrphanedTitleIdsAsync(
        IReadOnlyList<int> candidateTitleIds,
        CancellationToken cancellationToken = default)
    {
        if (candidateTitleIds.Count == 0)
        {
            return [];
        }

        return await context.Titles
            .AsNoTracking()
            .Where(t => candidateTitleIds.Contains(t.Id)
                        && !context.TitleSourceLinks.Any(l => l.TitleId == t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }
}
