using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Romd.Persistence;

namespace Romd.Infrastructure.Catalog;

/// <summary>
///     Derives a title's backing sources from entry-level links. Display names are composed
///     per kind at read time: DAT names live on versions (the Active version's name), so the
///     catalog row carries none — display composition here is not identity coupling.
/// </summary>
public sealed class TitleSourceReferenceReader(RomdDbContext context) : ITitleSourceReferenceReader
{
    public async Task<IReadOnlyList<TitleSourceReference>> GetReferencesAsync(
        int titleId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
                from link in context.TitleSourceLinks.AsNoTracking()
                join entry in context.SourceEntries.AsNoTracking()
                    on link.SourceEntryId equals entry.Id
                join source in context.CatalogSources.AsNoTracking()
                    on entry.CatalogSourceId equals source.Id
                where link.TitleId == titleId
                group source by new { source.Id, source.Kind, source.Status, source.Name } into grouped
                select new
                {
                    grouped.Key.Id,
                    grouped.Key.Kind,
                    grouped.Key.Status,
                    grouped.Key.Name,
                    EntryCount = grouped.Count()
                })
            .ToListAsync(cancellationToken);

        var datCatalogSourceIds = rows
            .Where(r => r.Kind == nameof(CatalogSourceKind.Dat))
            .Select(r => r.Id)
            .ToList();

        var datVersions = await (from datSource in context.DatSources
            join datFile in context.DatFiles on datSource.Id equals datFile.DatSourceId
            where datCatalogSourceIds.Contains(datSource.CatalogSourceId) && datFile.Lifecycle == nameof(DatFileLifecycle.Active)
            select new { datSource.CatalogSourceId, datFile.Id, datFile.Name, datFile.PlatformId }).ToListAsync(cancellationToken);
        var datBySource = datVersions.ToDictionary(d => d.CatalogSourceId);
        var activeSources = await (from link in context.TitleSourceLinks
            join entry in context.SourceEntries on link.SourceEntryId equals entry.Id
            join source in context.CatalogSources on entry.CatalogSourceId equals source.Id
            where link.TitleId == titleId && source.Status == nameof(CatalogSourceStatus.Active)
                && (source.Kind != nameof(CatalogSourceKind.Dat) || context.DatGames.Any(g => g.SourceEntryId == entry.Id
                    && context.DatFiles.Any(d => d.Id == g.DatFileId && d.Lifecycle == nameof(DatFileLifecycle.Active))))
            select source.Id).Distinct().ToListAsync(cancellationToken);

        return rows
            .OrderBy(r => r.Id)
            .Select(r => new TitleSourceReference(
                r.Id,
                Enum.Parse<CatalogSourceKind>(r.Kind),
                r.Name ?? datBySource.GetValueOrDefault(r.Id)?.Name,
                Enum.Parse<CatalogSourceStatus>(r.Status),
                r.EntryCount,
                datBySource.GetValueOrDefault(r.Id)?.Id,
                datBySource.GetValueOrDefault(r.Id)?.PlatformId,
                activeSources.Contains(r.Id)))
            .ToList();
    }
}
