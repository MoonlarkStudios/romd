using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence.Queries;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Catalog;

public sealed class RomCatalogOwnershipReader(RomdDbContext context) :
    IRomCatalogMatchReader,
    IRomOwnershipImpactReader,
    IRomPayloadAssertionImpactReader
{
    public async Task<RomCatalogMatch> ReadAsync(
        Sha1 sha1,
        CancellationToken cancellationToken = default)
    {
        var titleIds = await GetTitleIdsBySha1Async(sha1, cancellationToken);
        var titlePlatformIds = titleIds.Count == 0
            ? []
            : await GetTitlePlatformIdsBySha1Async(sha1, cancellationToken);
        var biosPlatformIds = await GetBiosPlatformIdsBySha1Async(sha1, cancellationToken);

        return new RomCatalogMatch(titleIds, titlePlatformIds, biosPlatformIds);
    }

    public async Task<IReadOnlyList<int>> ReadTitlePlatformIdsAsync(
        int romFileId,
        CancellationToken cancellationToken = default)
    {
        return await GetTitlePlatformIdQuery(context.DatRoms.Where(rom => rom.RomFileId == romFileId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> ReadSourceEntryIdsByRomFileIdAsync(
        int romFileId,
        CancellationToken cancellationToken = default)
    {
        return await context.DatRoms
            .AsNoTracking()
            .Where(rom => rom.RomFileId == romFileId)
            .Join(
                context.DatGames,
                rom => rom.DatGameId,
                game => game.Id,
                (_, game) => game.SourceEntryId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> ReadSourceEntryIdsBySha1Async(
        Sha1 sha1,
        CancellationToken cancellationToken = default)
    {
        return await context.DatRoms
            .AsNoTracking()
            .Where(rom => rom.Sha1 == sha1)
            .Join(
                context.DatGames,
                rom => rom.DatGameId,
                game => game.Id,
                (_, game) => game.SourceEntryId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<int>> GetTitleIdsBySha1Async(
        Sha1 sha1,
        CancellationToken cancellationToken)
    {
        return await context.DatRoms
            .AsNoTracking()
            .Where(rom => rom.Sha1 == sha1)
            .Join(
                context.DatGames,
                rom => rom.DatGameId,
                game => game.Id,
                (_, game) => game.SourceEntryId)
            .Join(
                context.EffectiveTitleSourceLinks(),
                sourceEntryId => sourceEntryId,
                link => link.SourceEntryId,
                (_, link) => link.TitleId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<int>> GetTitlePlatformIdsBySha1Async(
        Sha1 sha1,
        CancellationToken cancellationToken)
    {
        return await GetTitlePlatformIdQuery(context.DatRoms.Where(rom => rom.Sha1 == sha1))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<int>> GetBiosPlatformIdsBySha1Async(
        Sha1 sha1,
        CancellationToken cancellationToken)
    {
        // BIOS matching honors source status through the game's entry: a disabled authority
        // must not route ingest or claim catalog membership.
        return await (
                from rom in context.DatRoms.AsNoTracking()
                where rom.Sha1 == sha1
                join game in context.DatGames.AsNoTracking()
                    on rom.DatGameId equals game.Id
                join entry in context.EffectiveSourceEntries()
                    on game.SourceEntryId equals entry.Id
                join mapping in context.BiosGameMappings.AsNoTracking()
                    on rom.DatGameId equals mapping.DatGameId
                join bios in context.Bios.AsNoTracking()
                    on mapping.BiosId equals bios.Id
                select bios.PlatformId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private IQueryable<int> GetTitlePlatformIdQuery(IQueryable<DatRomEntity> datRoms)
    {
        return datRoms
            .AsNoTracking()
            .Join(
                context.DatGames,
                rom => rom.DatGameId,
                game => game.Id,
                (_, game) => game.SourceEntryId)
            .Join(
                context.EffectiveTitleSourceLinks(),
                sourceEntryId => sourceEntryId,
                link => link.SourceEntryId,
                (_, link) => link.TitleId)
            .Join(
                context.Titles,
                titleId => titleId,
                title => title.Id,
                (_, title) => title.PlatformId)
            .Distinct();
    }
}
