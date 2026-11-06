using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Orphan policy over titles that lost their source backing. Shares the artwork title-lock
///     protocol, so curation cannot commit between the retention decision and deletion.
/// </summary>
public sealed class TitleOrphanRepository : ITitleOrphanCleanup
{
    private readonly RomdDbContext _context;

    public TitleOrphanRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<int>> GetOrphanedTitleIdsAsync(
        DateTimeOffset createdBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _context.Titles
            .AsNoTracking()
            .Where(t => t.CatalogState != TitleCatalogState.UserOnly
                        && t.CreatedAt < createdBefore
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == t.Id))
            .OrderBy(t => t.Id)
            .Take(limit)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> HasSourceEntriesAsync(int titleId, CancellationToken cancellationToken = default) =>
        _context.TitleSourceLinks.AnyAsync(link => link.TitleId == titleId, cancellationToken);

    public async Task<bool> DeleteOrRetainAsync(int titleId, CancellationToken cancellationToken = default)
    {
        // Every mutation below re-checks zero links at mutation time: a concurrent
        // derivation may have re-linked the title after the caller's orphan check, and a
        // re-linked title must lose nothing. That promise only holds when the statements
        // are atomic, so a caller-owned transaction is an enforced precondition
        // (repository-owned transactions are ledgered debt).
        if (_context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "DeleteOrRetainAsync mutates releases and the title in separate statements " +
                "and requires a caller-owned transaction for atomicity.");
        }

        // Share the artwork title-lock protocol so Apply cannot commit curation
        // between the retention decision and orphan deletion.
        await _context.Titles.Where(t => t.Id == titleId).ExecuteUpdateAsync(
            setters => setters.SetProperty(t => t.RetainWithoutCatalog, t => t.RetainWithoutCatalog),
            cancellationToken);

        bool hasUserState = await _context.Titles
            .AsNoTracking()
            .Where(t => t.Id == titleId)
            .Select(t =>
                t.RetainWithoutCatalog ||
                _context.DiscSets.Any(set => set.Decision != "proposed" && set.TitleIds.Contains(titleId)) ||
                _context.TrackedTitles.Any(tracked => tracked.TitleId == titleId) ||
                t.FieldSourceOverridesJson != "{}" ||
                t.FieldProvenanceJson != "{}" ||
                t.ScreenshotPrefsJson != "{}" ||
                _context.TitleExternalIds.Any(e => e.TitleId == titleId && e.IsConfirmed) ||
                _context.TitleMedia.Any(m => m.TitleId == titleId && m.IsPrimary) ||
                _context.ArtworkAssets.Any(a => a.TitleId == titleId) ||
                _context.ArtworkSelections.Any(s => s.TitleId == titleId &&
                    (s.Mode == ArtworkSelectionMode.Pinned || s.PendingRequestId != null)) ||
                _context.CollectionItems.Any(c => c.TitleId == titleId))
            .FirstOrDefaultAsync(cancellationToken);

        if (hasUserState)
        {
            await _context.Titles
                .Where(t => t.Id == titleId
                            && !_context.TitleSourceLinks.Any(l => l.TitleId == titleId))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.CatalogState, Domain.Catalog.TitleCatalogState.UserOnly),
                    cancellationToken);
            return false;
        }

        // Release identity is fingerprint-stable and public (CatalogReleaseEntity): a
        // release another surviving source still asserts must keep its row and ID, so it is
        // re-pointed to a surviving linked title (projection recovery re-derives the true
        // representative on the next rebuild). Only releases nobody else asserts are stale
        // rows and are deleted; the Restrict FK makes hard-deleting a projected title this
        // deliberate act. MaterializedLibraryReleases rows cascade with the title.
        await _context.CatalogReleases
            .Where(r => r.CatalogTitleId == titleId
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == titleId)
                        && _context.CatalogReleaseSources.Any(s =>
                            s.CatalogReleaseId == r.Id
                            && _context.TitleSourceLinks.Any(l =>
                                l.SourceEntryId == s.SourceEntryId && l.TitleId != titleId)))
            .ExecuteUpdateAsync(
                u => u.SetProperty(
                    r => r.CatalogTitleId,
                    r => _context.CatalogReleaseSources
                        .Where(s => s.CatalogReleaseId == r.Id)
                        .SelectMany(s => _context.TitleSourceLinks
                            .Where(l => l.SourceEntryId == s.SourceEntryId && l.TitleId != titleId))
                        .OrderBy(l => l.TitleId)
                        .Select(l => l.TitleId)
                        .First()),
                cancellationToken);

        await _context.CatalogReleases
            .Where(r => r.CatalogTitleId == titleId
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == titleId))
            .ExecuteDeleteAsync(cancellationToken);

        return await _context.Titles
            .Where(t => t.Id == titleId
                        && !_context.TitleSourceLinks.Any(l => l.TitleId == titleId))
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }
}
