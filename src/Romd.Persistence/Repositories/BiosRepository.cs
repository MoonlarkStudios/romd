using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class BiosRepository : IBiosRepository
{
    private readonly RomdDbContext _context;

    public BiosRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<Dictionary<string, Bios>> GetByNormalizedNamesAsync(
        int platformId,
        IEnumerable<string> normalizedNames,
        CancellationToken cancellationToken = default)
    {
        var nameList = normalizedNames.ToList();
        if (nameList.Count == 0)
        {
            return new Dictionary<string, Bios>();
        }

        var entities = await _context.Bios
            .Where(b => b.PlatformId == platformId && nameList.Contains(b.NormalizedName))
            .ToListAsync(cancellationToken);

        return entities.ToDictionary(e => e.NormalizedName, e => e.ToDomain());
    }

    public async Task<IReadOnlyList<Bios>> AddRangeAsync(
        IReadOnlyList<Bios> bios,
        CancellationToken cancellationToken = default)
    {
        if (bios.Count == 0)
        {
            return [];
        }

        var entities = bios.Select(BiosEntity.FromDomain).ToList();
        _context.Bios.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task AddGameMappingsAsync(
        IReadOnlyList<(int DatGameId, int BiosId)> mappings,
        CancellationToken cancellationToken = default)
    {
        if (mappings.Count == 0)
        {
            return;
        }

        var entities = mappings
            .Select(m => new BiosGameMappingEntity { DatGameId = m.DatGameId, BiosId = m.BiosId })
            .ToList();

        _context.BiosGameMappings.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BiosOwnership>> GetByPlatformWithOwnershipAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        var biosList = await _context.Bios
            .Where(b => b.PlatformId == platformId)
            .OrderBy(b => b.Name)
            .ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.Name })
            .ToListAsync(cancellationToken);

        if (biosList.Count == 0)
        {
            return [];
        }

        var biosIds = biosList.Select(b => b.Id).ToList();

        // Pull every mapped ROM with its hash, DAT-declared size, and link. Volume is tiny (a platform
        // has a handful of BIOS), so distinct-by-SHA-1 is resolved in memory to dedupe the same firmware
        // listed across multiple DAT revisions. A ROM counts as owned when any of its DatRom rows links
        // a RomFile — the ingestion pipeline links all rows sharing a SHA-1 together.
        var romRows = await (
                from m in _context.BiosGameMappings
                where biosIds.Contains(m.BiosId)
                join r in _context.DatRoms on m.DatGameId equals r.DatGameId
                select new { m.BiosId, r.Sha1, r.Size, r.RomFileId })
            .ToListAsync(cancellationToken);

        // Resolve actual on-disk CAS bytes for the owned ROMs (RomFile -> Files). One batched lookup.
        var ownedRomFileIds = romRows
            .Where(x => x.RomFileId.HasValue)
            .Select(x => x.RomFileId!.Value)
            .Distinct()
            .ToList();

        var onDiskByRomFileId = ownedRomFileIds.Count == 0
            ? new Dictionary<int, long>()
            : await _context.RomFiles
                .Where(rf => ownedRomFileIds.Contains(rf.Id))
                .Join(_context.Files, rf => rf.FileId, f => f.Id, (rf, f) => new { rf.Id, f.SizeOnDisk })
                .ToDictionaryAsync(x => x.Id, x => x.SizeOnDisk, cancellationToken);

        var statsByBios = romRows
            .Where(x => x.Sha1.HasValue)
            .GroupBy(x => x.BiosId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    int total = 0;
                    int owned = 0;
                    long requiredBytes = 0;
                    long ownedBytes = 0;
                    long onDiskBytes = 0;

                    foreach (var shaGroup in group.GroupBy(x => x.Sha1!.Value))
                    {
                        total++;
                        // Same content (SHA-1) declares the same size across DAT revisions.
                        long size = shaGroup.Max(x => x.Size);
                        requiredBytes += size;

                        var ownedRow = shaGroup.FirstOrDefault(x => x.RomFileId.HasValue);
                        if (ownedRow is not null)
                        {
                            owned++;
                            ownedBytes += size;
                            onDiskBytes += onDiskByRomFileId.TryGetValue(ownedRow.RomFileId!.Value, out long d)
                                ? d
                                : size;
                        }
                    }

                    return (Total: total, Owned: owned, RequiredBytes: requiredBytes, OwnedBytes: ownedBytes,
                        OnDiskBytes: onDiskBytes);
                });

        return biosList
            .Select(b =>
            {
                var s = statsByBios.TryGetValue(b.Id, out var c)
                    ? c
                    : (Total: 0, Owned: 0, RequiredBytes: 0L, OwnedBytes: 0L, OnDiskBytes: 0L);
                return new BiosOwnership(
                    b.Id, platformId, b.Name, s.Total, s.Owned, s.RequiredBytes, s.OwnedBytes, s.OnDiskBytes);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<UnmappedBiosGame>> GetUnmappedBiosGamesAsync(
        CancellationToken cancellationToken = default)
    {
        return await (
                from g in _context.DatGames
                where g.IsBios
                join d in _context.DatFiles on g.DatFileId equals d.Id
                where d.PlatformId != null
                where !_context.BiosGameMappings.Any(m => m.DatGameId == g.Id)
                select new UnmappedBiosGame(g.Id, d.PlatformId!.Value, g.Name))
            .ToListAsync(cancellationToken);
    }
}
