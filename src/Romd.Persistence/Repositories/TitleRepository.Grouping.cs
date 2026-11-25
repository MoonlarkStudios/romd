using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed partial class TitleRepository
{
    public async Task<bool> ValidateDatGroupingAsync(int sourceTitleId, int targetTitleId, string evidence, CancellationToken cancellationToken)
    {
        RequireTransaction();
        await CatalogTopologyFence.AcquireAsync(_context, cancellationToken);

        var reader = new DatTitleGroupingReader(_context);
        var source = (await reader.ReadAsync(sourceTitleId, cancellationToken))?.Suggestion;
        if (source?.ParentTitleId == targetTitleId && source.Fingerprint == evidence)
            return true;

        var target = (await reader.ReadAsync(targetTitleId, cancellationToken))?.Suggestion;
        return target?.ParentTitleId == sourceTitleId && target.Fingerprint == evidence;
    }

    public async Task ReassignTitleReferencesAsync(int sourceTitleId, int targetTitleId, CancellationToken cancellationToken)
    {
        RequireTransaction();
        await CatalogTopologyFence.AcquireAsync(_context, cancellationToken);

        // Flatten earlier redirects so even long sequences of merges need one lookup.
        await _context.TitleMergeRedirects.Where(redirect => redirect.TargetTitleId == sourceTitleId)
            .ExecuteUpdateAsync(set => set.SetProperty(redirect => redirect.TargetTitleId, targetTitleId), cancellationToken);
        _context.TitleMergeRedirects.Add(new TitleMergeRedirectEntity
        {
            SourceTitleId = sourceTitleId,
            TargetTitleId = targetTitleId
        });

        // Transfer release ownership before deleting the source. The catalog rebuild keeps
        // these rows by fingerprint and refreshes their provenance and library projection.
        await _context.CatalogReleases.Where(release => release.CatalogTitleId == sourceTitleId)
            .ExecuteUpdateAsync(set => set.SetProperty(release => release.CatalogTitleId, targetTitleId), cancellationToken);
        await _context.PlaySessions.Where(session => session.TitleId == sourceTitleId)
            .ExecuteUpdateAsync(set => set.SetProperty(session => session.TitleId, targetTitleId), cancellationToken);

        // For duplicate memberships, retain the surviving title's position and prefer its
        // note. Source-only memberships retain their identity, note and position.
        var sourceItems = _context.CollectionItems.Where(item => item.TitleId == sourceTitleId);
        var targetItems = _context.CollectionItems.Where(item => item.TitleId == targetTitleId);
        await targetItems.Where(item => item.Note == null &&
                sourceItems.Any(source => source.CollectionId == item.CollectionId && source.Note != null))
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Note,
                item => sourceItems.Where(source => source.CollectionId == item.CollectionId)
                    .Select(source => source.Note).FirstOrDefault()), cancellationToken);
        await sourceItems.Where(item => targetItems.Any(target => target.CollectionId == item.CollectionId))
            .ExecuteDeleteAsync(cancellationToken);
        await sourceItems.ExecuteUpdateAsync(set => set.SetProperty(item => item.TitleId, targetTitleId), cancellationToken);
    }
}
