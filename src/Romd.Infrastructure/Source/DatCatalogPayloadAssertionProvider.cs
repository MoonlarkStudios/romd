using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Source;

/// <summary>
///     DAT adapter for the narrow payload assertion boundary. No title availability policy
///     lives here; it reports only whether a requested entry has a linked local ROM payload.
/// </summary>
public sealed class DatCatalogPayloadAssertionProvider(RomdDbContext context)
    : ICatalogPayloadAssertionProvider
{
    public CatalogSourceKind SourceKind => CatalogSourceKind.Dat;

    public Task SynchronizeCatalogSourceAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default) =>
        SynchronizeAsync(
            context.SourceEntries.Where(entry => entry.CatalogSourceId == catalogSourceId),
            cancellationToken);

    public Task SynchronizePlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default) =>
        SynchronizeAsync(
            context.SourceEntries.Where(entry => entry.PlatformId == platformId),
            cancellationToken);

    public async Task<ImmutableArray<CatalogPayloadAssertion>> ReadAsync(
        IReadOnlyCollection<int> sourceEntryIds,
        CancellationToken cancellationToken = default)
    {
        if (sourceEntryIds.Count == 0)
        {
            return ImmutableArray<CatalogPayloadAssertion>.Empty;
        }

        var ids = sourceEntryIds.Distinct().ToList();
        var assertedEntryIds = await (
                from game in context.DatGames.AsNoTracking()
                    .Where(IntegerKeyPredicate.Create<DatGameEntity>(ids, row => row.SourceEntryId))
                join entry in context.SourceEntries.AsNoTracking()
                    on game.SourceEntryId equals entry.Id
                join source in context.CatalogSources.AsNoTracking()
                    on entry.CatalogSourceId equals source.Id
                where source.Kind == nameof(CatalogSourceKind.Dat)
                      && game.Roms.Any(rom => rom.RomFileId != null)
                select game.SourceEntryId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (assertedEntryIds.Count == 0)
        {
            return ImmutableArray<CatalogPayloadAssertion>.Empty;
        }

        var assertedTitles = await context.TitleSourceLinks
            .AsNoTracking()
            .Where(IntegerKeyPredicate.Create<TitleSourceLinkEntity>(
                assertedEntryIds,
                link => link.SourceEntryId))
            .ToDictionaryAsync(link => link.SourceEntryId, link => link.TitleId, cancellationToken);

        return assertedEntryIds
            .Select(id => new CatalogPayloadAssertion(
                id,
                assertedTitles.TryGetValue(id, out int titleId) ? titleId : null,
                true))
            .ToImmutableArray();
    }

    private Task SynchronizeAsync(
        IQueryable<SourceEntryEntity> scope,
        CancellationToken cancellationToken)
    {
        var datScope = scope.Where(entry => context.CatalogSources.Any(source =>
            source.Id == entry.CatalogSourceId && source.Kind == nameof(CatalogSourceKind.Dat)));
        return datScope
            .Where(entry => entry.HasLocalPayload != context.DatGames.Any(game =>
                game.SourceEntryId == entry.Id && game.Roms.Any(rom => rom.RomFileId != null)))
            .ExecuteUpdateAsync(
                update => update.SetProperty(
                    entry => entry.HasLocalPayload,
                    entry => context.DatGames.Any(game =>
                        game.SourceEntryId == entry.Id && game.Roms.Any(rom => rom.RomFileId != null))),
                cancellationToken);
    }
}
