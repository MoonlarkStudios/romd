using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class RomRepository : IRomRepository
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();

    private readonly RomdDbContext _context;

    public RomRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<RomFile?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var result = await _context.RomFiles
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size })
            .FirstOrDefaultAsync(cancellationToken);

        return result is null ? null : result.Entity.ToDomain(result.Size);
    }

    public async Task<RomFile?> GetBySha1Async(Sha1 sha1, CancellationToken cancellationToken = default)
    {
        var result = await _context.RomFiles
            .AsNoTracking()
            .Where(r => r.Sha1 == sha1)
            .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size })
            .FirstOrDefaultAsync(cancellationToken);

        return result is null ? null : result.Entity.ToDomain(result.Size);
    }

    public async Task<RomFile?> GetByHashAsync(
        Sha1? sha1 = null,
        Md5? md5 = null,
        Crc32? crc32 = null,
        long? size = null,
        CancellationToken cancellationToken = default)
    {
        if (sha1.HasValue)
        {
            var result = await _context.RomFiles
                .AsNoTracking()
                .Where(r => r.Sha1 == sha1)
                .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size })
                .FirstOrDefaultAsync(cancellationToken);

            return result is null ? null : result.Entity.ToDomain(result.Size);
        }

        if (md5.HasValue)
        {
            var query = _context.RomFiles.AsNoTracking().Where(r => r.Md5 == md5)
                .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size });

            if (size.HasValue)
            {
                query = query.Where(r => r.Size == size.Value);
            }

            var result = await query.FirstOrDefaultAsync(cancellationToken);
            return result is null ? null : result.Entity.ToDomain(result.Size);
        }

        if (crc32.HasValue)
        {
            var query = _context.RomFiles.AsNoTracking().Where(r => r.Crc32 == crc32)
                .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size });

            if (size.HasValue)
            {
                query = query.Where(r => r.Size == size.Value);
            }

            var result = await query.FirstOrDefaultAsync(cancellationToken);
            return result is null ? null : result.Entity.ToDomain(result.Size);
        }

        return null;
    }

    public async Task<RomFile> AddAsync(RomFile romFile, CancellationToken cancellationToken = default)
    {
        var entity = RomFileEntity.FromDomain(romFile);
        _context.RomFiles.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        long fileSize = await _context.Files
            .Where(f => f.Id == entity.FileId)
            .Select(f => f.Size)
            .FirstAsync(cancellationToken);

        return entity.ToDomain(fileSize);
    }

    public Task AddStagedAsync(RomFile romFile, CancellationToken cancellationToken = default)
    {
        _context.RomFiles.Add(RomFileEntity.FromDomain(romFile));
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await _context.RomFiles
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> DeleteUnidentifiedAsync(CancellationToken cancellationToken = default)
    {
        // Unidentified == no DatRom links (same predicate as GetByStatus(Unidentified)). One bulk
        // statement; the now-orphaned CAS files are reclaimed by the recurring cleanup job.
        return _context.RomFiles
            .Where(rf => !_context.DatRoms.Any(r => r.RomFileId == rf.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<bool> ExistsBySha1Async(Sha1 sha1, CancellationToken cancellationToken = default)
    {
        return _context.RomFiles
            .AnyAsync(r => r.Sha1 == sha1, cancellationToken);
    }

    public async Task<IReadOnlyList<RomFile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.RomFiles
            .AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size })
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.Entity.ToDomain(e.Size)).ToList();
    }

    public async Task<IReadOnlyList<RomFile>> GetOrphanedAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.RomFiles
            .AsNoTracking()
            .Where(rf => !_context.DatRoms.Any(r => r.RomFileId == rf.Id))
            .OrderByDescending(rf => rf.Id)
            .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size })
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.Entity.ToDomain(e.Size)).ToList();
    }

    public async Task<PagedList<RomFile>> GetByStatusAsync(
        RomCatalogStatus status,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        int? lastId = DecodeCursor(cursor);

        var baseQuery = status switch
        {
            RomCatalogStatus.Unidentified => _context.RomFiles
                .AsNoTracking()
                .Where(rf => !_context.DatRoms.Any(r => r.RomFileId == rf.Id)),

            RomCatalogStatus.Cataloged => _context.RomFiles
                .AsNoTracking()
                .Where(rf => _context.DatRoms
                    .Where(r => r.RomFileId == rf.Id)
                    .Join(_context.DatGames, r => r.DatGameId, g => g.Id, (r, g) => g)
                    .Any(g => _context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId))),

            RomCatalogStatus.Unrouted => _context.RomFiles
                .AsNoTracking()
                .Where(rf =>
                    _context.DatRoms.Any(r => r.RomFileId == rf.Id) &&
                    !_context.DatRoms
                        .Where(r => r.RomFileId == rf.Id)
                        .Join(_context.DatGames, r => r.DatGameId, g => g.Id, (r, g) => g)
                        .Any(g => _context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId))),

            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

        if (lastId.HasValue)
        {
            baseQuery = baseQuery.Where(rf => rf.Id > lastId.Value);
        }

        var entities = await baseQuery
            .OrderBy(rf => rf.Id)
            .Take(limit + 1)
            .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size })
            .ToListAsync(cancellationToken);

        bool hasNextPage = entities.Count > limit;
        string? nextCursor = null;

        if (hasNextPage)
        {
            var lastItem = entities[limit - 1];
            nextCursor = EncodeCursor(lastItem.Entity.Id);
            entities.RemoveAt(limit);
        }

        var items = entities.Select(e => e.Entity.ToDomain(e.Size)).ToList();
        return new PagedList<RomFile>(items, nextCursor, hasNextPage);
    }

    public async Task<CollectionStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        int totalRomFiles = await _context.RomFiles.CountAsync(cancellationToken);
        var sizeTotals = totalRomFiles > 0
            ? await _context.RomFiles
                .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => f)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Size = g.Sum(f => f.Size),
                    SizeOnDisk = g.Sum(f => f.SizeOnDisk)
                })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        long totalSize = sizeTotals?.Size ?? 0L;
        long totalSizeOnDisk = sizeTotals?.SizeOnDisk ?? 0L;

        int unidentifiedCount = await _context.RomFiles
            .Where(rf => !_context.DatRoms.Any(r => r.RomFileId == rf.Id))
            .CountAsync(cancellationToken);

        // Count the distinct held ROM identities reached from effective catalog links.
        // Starting at the links avoids nested correlated EXISTS for each stored ROM.
        int catalogedCount = await (
                from link in _context.EffectiveTitleSourceLinks()
                join game in _context.DatGames on link.SourceEntryId equals game.SourceEntryId
                join rom in _context.DatRoms on game.Id equals rom.DatGameId
                where rom.RomFileId != null
                select rom.RomFileId)
            .Distinct()
            .CountAsync(cancellationToken);

        int unroutedCount = totalRomFiles - unidentifiedCount - catalogedCount;

        var platformBreakdown = await GetPlatformBreakdownAsync(cancellationToken);

        return new CollectionStats
        {
            TotalRomFiles = totalRomFiles,
            UnidentifiedCount = unidentifiedCount,
            UnroutedCount = unroutedCount,
            CatalogedCount = catalogedCount,
            TotalSizeBytes = totalSize,
            TotalSizeOnDiskBytes = totalSizeOnDisk,
            PlatformBreakdown = platformBreakdown
        };
    }

    public async Task<RomCatalogStatus> GetStatusAsync(int romFileId, CancellationToken cancellationToken = default)
    {
        bool hasDatRom = await _context.DatRoms.AnyAsync(r => r.RomFileId == romFileId, cancellationToken);
        if (!hasDatRom)
        {
            return RomCatalogStatus.Unidentified;
        }

        bool isCataloged = await _context.DatRoms
            .Where(r => r.RomFileId == romFileId)
            .Join(_context.DatGames, r => r.DatGameId, g => g.Id, (r, g) => g)
            .AnyAsync(g => _context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId), cancellationToken);

        return isCataloged ? RomCatalogStatus.Cataloged : RomCatalogStatus.Unrouted;
    }

    public async Task<bool> IsAccessibleAsync(int romFileId, int? libraryId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId is null)
        {
            return true;
        }

        int id = libraryId.Value;
        bool hasValidLibrary = await _context.Libraries.AnyAsync(
            library => library.Id == id && library.ConfigurationState == ValidConfigurationState,
            cancellationToken);

        if (!hasValidLibrary)
        {
            return false;
        }

        // ROM is accessible if it's not linked to any titles (unidentified). Truth-level
        // deliberately: a dormant ROM (linked, but every backing source non-Active) is NOT
        // unidentified — it must fail closed through the materialized-library authorization
        // below, not gain access by its source being disabled.
        bool hasNoTitles = !await _context.DatRoms
            .Where(r => r.RomFileId == romFileId)
            .Join(_context.DatGames, r => r.DatGameId, g => g.Id, (r, g) => g)
            .AnyAsync(g => _context.TitleSourceLinks.Any(l => l.SourceEntryId == g.SourceEntryId), cancellationToken);

        if (hasNoTitles)
        {
            return true;
        }

        // ROM is accessible if any of its linked releases are accessible in the materialized library.
        return await _context.DatRoms
            .Where(r => r.RomFileId == romFileId)
            .Select(r => r.DatGameId)
            .AnyAsync(datGameId =>
                _context.MaterializedLibraryReleases.Any(release =>
                    release.LibraryId == id &&
                    release.DatGameId == datGameId &&
                    release.IsExposed &&
                    release.IsOwned),
                cancellationToken);
    }

    public async Task<IReadOnlyList<RomMatchData>> GetMatchesAsync(int romId, CancellationToken cancellationToken = default)
    {
        return await (from file in _context.DatRoms
            where file.RomFileId == romId
            join game in _context.DatGames on file.DatGameId equals game.Id
            join dat in _context.DatFiles on game.DatFileId equals dat.Id
            join link in _context.TitleSourceLinks on game.SourceEntryId equals link.SourceEntryId into links
            from link in links.DefaultIfEmpty()
            join title in _context.Titles on link.TitleId equals title.Id into titles
            from title in titles.DefaultIfEmpty()
            join platform in _context.Platforms on (title == null ? dat.PlatformId : (int?)title.PlatformId) equals (int?)platform.Id into platforms
            from platform in platforms.DefaultIfEmpty()
            orderby platform.Name, title.Name, game.Name, dat.Id, file.Id
            select new RomMatchData(dat.Id, dat.Name, game.Id, game.Name, file.Name,
                title == null ? null : title.Id, title == null ? null : title.Name,
                platform == null ? null : platform.Id, platform == null ? null : platform.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedList<RomWithStatus>> ListAsync(
        RomCatalogStatus? status = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default,
        string? search = null)
    {
        limit = Math.Clamp(limit, 1, 200);
        int? lastId = DecodeCursor(cursor);

        var query = _context.RomFiles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(rf => rf.OriginalFilename.ToLower().Contains(term));
        }

        if (status is not null)
        {
            query = status switch
            {
                RomCatalogStatus.Unidentified => query
                    .Where(rf => !_context.DatRoms.Any(r => r.RomFileId == rf.Id)),
                RomCatalogStatus.Cataloged => query
                    .Where(rf => _context.DatRoms
                        .Where(r => r.RomFileId == rf.Id)
                        .Join(_context.DatGames, r => r.DatGameId, g => g.Id, (r, g) => g)
                        .Any(g => _context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId))),
                RomCatalogStatus.Unrouted => query
                    .Where(rf =>
                        _context.DatRoms.Any(r => r.RomFileId == rf.Id) &&
                        !_context.DatRoms
                            .Where(r => r.RomFileId == rf.Id)
                            .Join(_context.DatGames, r => r.DatGameId, g => g.Id, (r, g) => g)
                            .Any(g => _context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId))),
                _ => query
            };
        }

        if (lastId.HasValue)
        {
            query = query.Where(rf => rf.Id > lastId.Value);
        }

        var entities = await query
            .OrderBy(rf => rf.Id)
            .Take(limit + 1)
            .Join(_context.Files, r => r.FileId, f => f.Id, (r, f) => new { Entity = r, f.Size })
            .Select(x => new
            {
                x.Entity,
                x.Size,
                HasDatRom = _context.DatRoms.Any(r => r.RomFileId == x.Entity.Id),
                IsCataloged = _context.DatRoms
                    .Where(r => r.RomFileId == x.Entity.Id)
                    .Join(_context.DatGames, r => r.DatGameId, g => g.Id, (r, g) => g)
                    .Any(g => _context.EffectiveTitleSourceLinks().Any(l => l.SourceEntryId == g.SourceEntryId))
            })
            .ToListAsync(cancellationToken);

        bool hasNextPage = entities.Count > limit;
        string? nextCursor = null;

        if (hasNextPage)
        {
            var lastItem = entities[limit - 1];
            nextCursor = EncodeCursor(lastItem.Entity.Id);
            entities.RemoveAt(limit);
        }

        var items = entities.Select(item =>
        {
            var status = !item.HasDatRom
                ? RomCatalogStatus.Unidentified
                : item.IsCataloged
                    ? RomCatalogStatus.Cataloged
                    : RomCatalogStatus.Unrouted;
            return new RomWithStatus(item.Entity.ToDomain(item.Size), status);
        }).ToList();

        return new PagedList<RomWithStatus>(items, nextCursor, hasNextPage);
    }

    public async Task<CoverageBreakdown> GetCoverageBreakdownAsync(CancellationToken cancellationToken = default)
    {
        // Coverage is measured against the curated (tracked) set, not the entire DAT catalog:
        // the denominator is tracked titles and the numerator is tracked titles that are owned.
        int expectedTitleCount = await _context.TrackedTitles.CountAsync(cancellationToken);

        // Aggregate each tracked title's effective ROM rows once. Correlated counts here
        // are duplicated into every outer predicate by SQL translation, multiplying work
        // across the entire tracked collection on every statistics refresh.
        var titleRomCounts =
            from link in _context.EffectiveTitleSourceLinks()
            join tracked in _context.TrackedTitles on link.TitleId equals tracked.TitleId
            join game in _context.DatGames on link.SourceEntryId equals game.SourceEntryId
            join rom in _context.DatRoms on game.Id equals rom.DatGameId
            group rom by link.TitleId
            into roms
            select new
            {
                TotalDatRoms = roms.Count(),
                MatchedDatRoms = roms.Count(rom => rom.RomFileId != null)
            };

        var counts = await titleRomCounts
            .GroupBy(x => 1)
            .Select(g => new
            {
                Complete = g.Count(x => x.MatchedDatRoms == x.TotalDatRoms),
                Partial = g.Count(x => x.MatchedDatRoms > 0 && x.MatchedDatRoms < x.TotalDatRoms)
            })
            .FirstOrDefaultAsync(cancellationToken);

        int completeCount = counts?.Complete ?? 0;
        int partialCount = counts?.Partial ?? 0;

        return new CoverageBreakdown(expectedTitleCount, completeCount + partialCount, completeCount, partialCount);
    }

    public async Task<IReadOnlyList<PlatformBreakdown>> GetPlatformBreakdownAsync(
        CancellationToken cancellationToken = default)
    {
        var trackedTitleStats =
            from title in _context.Titles.AsNoTracking()
            join tracked in _context.TrackedTitles on title.Id equals tracked.TitleId
            group title by title.PlatformId
            into titles
            select new
            {
                PlatformId = titles.Key,
                TotalCount = (int?)titles.Count(),
                LocalPayloadCount = (int?)titles.Count(title => title.HasLocalPayload)
            };

        var platformStats = await _context.Platforms
            .AsNoTracking()
            .GroupJoin(
                trackedTitleStats,
                platform => platform.Id,
                stats => stats.PlatformId,
                (platform, stats) => new { Platform = platform, Stats = stats })
            .SelectMany(
                item => item.Stats.DefaultIfEmpty(),
                (item, stats) => new PlatformBreakdown
            {
                PlatformId = item.Platform.Id,
                PlatformName = item.Platform.Name,
                // Both counts share one grouped tracked-title scan. The outer platform join keeps
                // empty platforms visible with zeroes instead of issuing two scalar scans per row.
                LocalPayloadCount = stats!.LocalPayloadCount ?? 0,
                TotalCount = stats.TotalCount ?? 0
            })
            .ToListAsync(cancellationToken);

        return platformStats;
    }

    private static string EncodeCursor(int id) => id.ToString();

    private static int? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        return int.TryParse(cursor, out int id) ? id : null;
    }
}
