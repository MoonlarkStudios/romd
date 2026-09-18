using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Entities;
using Romd.Persistence.Extensions;

namespace Romd.Persistence.Repositories;

public sealed class DatRepository : IDatRepository, ITitleSourceAssignmentStore
{
    private static readonly string[] ReplaceDatTerminalPhases =
    [
        nameof(ReplaceDatPhase.Completed),
        nameof(ReplaceDatPhase.CompletedWithErrors),
        nameof(ReplaceDatPhase.Failed),
        nameof(ReplaceDatPhase.Cancelled)
    ];

    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;

    public DatRepository(RomdDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<DatFile?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DatFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<DatFile?> GetByFileIdAsync(int fileId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DatFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.FileId == fileId && d.Lifecycle != nameof(DatFileLifecycle.Superseded),
                cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<DatFile?> GetActiveBySourceIdAsync(int datSourceId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DatFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.DatSourceId == datSourceId && d.Lifecycle == nameof(DatFileLifecycle.Active),
                cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<DatFile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.DatFiles
            .AsNoTracking()
            .Where(d => d.Lifecycle == nameof(DatFileLifecycle.Active))
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<DatWithSize?> GetByIdWithSizeAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await (
                from d in _context.DatFiles.AsNoTracking()
                where d.Id == id
                join f in _context.Files on d.FileId equals f.Id into fj
                from f in fj.DefaultIfEmpty()
                join s in _context.DatSources on d.DatSourceId equals s.Id
                join c in _context.CatalogSources on s.CatalogSourceId equals c.Id
                select new
                {
                    Dat = d,
                    Size = (long?)f.Size,
                    SizeOnDisk = (long?)f.SizeOnDisk,
                    IsCompressed = (bool?)f.IsCompressed,
                    SourceStatus = c.Status,
                    CatalogSourceId = c.Id
                })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new DatWithSize(
                row.Dat.ToDomain(),
                row.Size,
                row.SizeOnDisk,
                row.IsCompressed,
                Enum.Parse<CatalogSourceStatus>(row.SourceStatus),
                row.CatalogSourceId);
    }

    public async Task<IReadOnlyList<DatWithSize>> GetAllWithSizeAsync(CancellationToken cancellationToken = default)
    {
        var rows = await (
                from d in _context.DatFiles.AsNoTracking()
                where d.Lifecycle == nameof(DatFileLifecycle.Active)
                join f in _context.Files on d.FileId equals f.Id into fj
                from f in fj.DefaultIfEmpty()
                join s in _context.DatSources on d.DatSourceId equals s.Id
                join c in _context.CatalogSources on s.CatalogSourceId equals c.Id
                orderby d.Name
                select new
                {
                    Dat = d,
                    Size = (long?)f.Size,
                    SizeOnDisk = (long?)f.SizeOnDisk,
                    IsCompressed = (bool?)f.IsCompressed,
                    SourceStatus = c.Status,
                    CatalogSourceId = c.Id
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new DatWithSize(
                r.Dat.ToDomain(),
                r.Size,
                r.SizeOnDisk,
                r.IsCompressed,
                Enum.Parse<CatalogSourceStatus>(r.SourceStatus),
                r.CatalogSourceId))
            .ToList();
    }

    public Task<IReadOnlyList<DatWithSourceStatus>> GetByPlatformIdAsync(int platformId,
        CancellationToken cancellationToken = default) =>
        GetActiveWithSourceStatusAsync(d => d.PlatformId == platformId, cancellationToken);

    public Task<IReadOnlyList<DatWithSourceStatus>> GetUnroutedAsync(CancellationToken cancellationToken = default) =>
        GetActiveWithSourceStatusAsync(d => d.PlatformId == null, cancellationToken);

    /// <summary>
    ///     Reads Active DATs with their catalog source status and id in one query, so a
    ///     concurrent DAT deletion can never surface a DAT without its source rows.
    /// </summary>
    private async Task<IReadOnlyList<DatWithSourceStatus>> GetActiveWithSourceStatusAsync(
        Expression<Func<DatFileEntity, bool>> predicate,
        CancellationToken cancellationToken)
    {
        var rows = await (
                from d in _context.DatFiles.AsNoTracking().Where(predicate)
                where d.Lifecycle == nameof(DatFileLifecycle.Active)
                join s in _context.DatSources on d.DatSourceId equals s.Id
                join c in _context.CatalogSources on s.CatalogSourceId equals c.Id
                orderby d.Name
                select new { Dat = d, SourceStatus = c.Status, CatalogSourceId = c.Id })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new DatWithSourceStatus(
                r.Dat.ToDomain(),
                Enum.Parse<CatalogSourceStatus>(r.SourceStatus),
                r.CatalogSourceId))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<int, int>> CountMatchedRomFilesByDatAsync(
        IReadOnlyList<int> datIds,
        CancellationToken cancellationToken = default)
    {
        if (datIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        // Distinct (DatFileId, RomFileId) pairs; grouped client-side because EF Core does not
        // translate GROUP BY with COUNT(DISTINCT ...).
        var pairs = await _context.DatRoms
            .AsNoTracking()
            .Where(r => r.RomFileId != null)
            .Join(
                _context.DatGames,
                rom => rom.DatGameId,
                game => game.Id,
                (rom, game) => new { game.DatFileId, rom.RomFileId })
            .Where(x => datIds.Contains(x.DatFileId))
            .Distinct()
            .ToListAsync(cancellationToken);

        return pairs
            .GroupBy(p => p.DatFileId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<DatFile> AddAsync(DatFile datFile, CancellationToken cancellationToken = default)
    {
        var entity = DatFileEntity.FromDomain(datFile);

        _context.DatFiles.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        // Return domain model with generated Id
        return entity.ToDomain();
    }

    public Task AddStagedAsync(DatFile datFile, DatSource source, CancellationToken cancellationToken = default)
    {
        var entity = DatFileEntity.FromDomain(datFile);
        entity.Source = DatSourceEntity.FromDomain(source);

        // Every DAT source realizes one neutral catalog source; DAT names stay on versions,
        // so the catalog row carries no name (see CatalogSourceEntity).
        entity.Source.CatalogSource = new CatalogSourceEntity
        {
            Kind = nameof(CatalogSourceKind.Dat),
            Status = nameof(CatalogSourceStatus.Active)
        };

        _context.DatFiles.Add(entity);
        return Task.CompletedTask;
    }

    public Task AddVersionStagedAsync(DatFile datFile, CancellationToken cancellationToken = default)
    {
        _context.DatFiles.Add(DatFileEntity.FromDomain(datFile));
        return Task.CompletedTask;
    }

    public async Task UpdateLifecycleStagedAsync(DatFile datFile, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DatFiles
            .AsTracking()
            .SingleAsync(d => d.Id == datFile.Id, cancellationToken);

        entity.Lifecycle = datFile.Lifecycle.ToString();
        entity.SupersededAt = datFile.SupersededAt;
    }

    public async Task DeleteGamesByDatFileIdAsync(int datFileId, CancellationToken cancellationToken = default)
    {
        await _context.DatGames
            .Where(g => g.DatFileId == datFileId)
            .ExecuteDeleteAsync(cancellationToken);

        await PruneUnreferencedDatSourceEntriesAsync(cancellationToken);
    }

    /// <summary>
    ///     Deletes DAT-kind source entries no version's game references anymore; their title
    ///     links cascade at the database. Runs after every game-graph deletion so a claim
    ///     never outlives the last provider payload asserting it (the pre-cutover semantics,
    ///     with version retention as the grace period). Scoped to DAT-kind sources so future
    ///     providers' entries are never touched.
    /// </summary>
    private async Task PruneUnreferencedDatSourceEntriesAsync(CancellationToken cancellationToken) =>
        await _context.SourceEntries
            .Where(e => _context.CatalogSources.Any(
                            c => c.Id == e.CatalogSourceId && c.Kind == nameof(CatalogSourceKind.Dat))
                        && !_context.DatGames.Any(g => g.SourceEntryId == e.Id))
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<int> DeleteSupersededVersionsBeyondMostRecentAsync(
        int datSourceId,
        CancellationToken cancellationToken = default)
    {
        // Keep winner selection inside the DELETE so retention is one statement: the winner is
        // chosen and the losers removed under the same row locks, leaving no window for another
        // writer (notably catalog recovery after activation marks the platform Dirty) between a
        // separate SELECT and the DELETE.
        var retainedVersion = _context.DatFiles
            .Where(d => d.DatSourceId == datSourceId && d.Lifecycle == nameof(DatFileLifecycle.Superseded))
            .OrderByDescending(d => d.SupersededAt)
            .ThenByDescending(d => d.Id)
            .Select(d => d.Id)
            .Take(1);

        int deleted = await _context.DatFiles
            .Where(d => d.DatSourceId == datSourceId
                        && d.Lifecycle == nameof(DatFileLifecycle.Superseded)
                        && !retainedVersion.Contains(d.Id))
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            await PruneUnreferencedDatSourceEntriesAsync(cancellationToken);
        }

        return deleted;
    }

    public async Task<int> DeleteAbandonedPendingVersionsAsync(
        DateTimeOffset importedBefore,
        CancellationToken cancellationToken = default)
    {
        // The DatFile row and its parsed graph go together: DatGames (and their children)
        // cascade at the database. The source anchor and its Active version are untouched.
        int deleted = await _context.DatFiles
            .Where(d => d.Lifecycle == nameof(DatFileLifecycle.PendingActivation)
                        && d.CreatedAt < importedBefore
                        && !_context.Jobs
                            .OfType<ReplaceDatJobEntity>()
                            .Any(j => j.NewDatId == d.Id && !ReplaceDatTerminalPhases.Contains(j.Phase)))
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            await PruneUnreferencedDatSourceEntriesAsync(cancellationToken);
        }

        return deleted;
    }

    public async Task<IReadOnlyList<int>> GetRoutedPlatformIdsWithAgedPendingVersionsAsync(
        DateTimeOffset importedBefore,
        CancellationToken cancellationToken = default) =>
        await _context.DatFiles
            .Where(d => d.PlatformId != null
                        && d.Lifecycle == nameof(DatFileLifecycle.PendingActivation)
                        && d.CreatedAt < importedBefore)
            .Select(d => d.PlatformId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<int>> GetSourceIdsExceedingSupersededRetentionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _context.DatFiles
            .Where(d => d.Lifecycle == nameof(DatFileLifecycle.Superseded))
            .GroupBy(d => d.DatSourceId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> HasSupersededVersionsBeyondRetentionAsync(
        int datSourceId,
        CancellationToken cancellationToken = default) =>
        _context.DatFiles
            .Where(d => d.DatSourceId == datSourceId
                        && d.Lifecycle == nameof(DatFileLifecycle.Superseded))
            .Skip(1)
            .AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<int>> GetRoutedPlatformIdsBySourceIdAsync(
        int datSourceId,
        CancellationToken cancellationToken = default) =>
        await _context.DatFiles
            .Where(d => d.DatSourceId == datSourceId && d.PlatformId != null)
            .Select(d => d.PlatformId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task DeleteSourceIfOrphanedAsync(int datSourceId, CancellationToken cancellationToken = default)
    {
        int? catalogSourceId = await _context.DatSources
            .Where(s => s.Id == datSourceId && !_context.DatFiles.Any(d => d.DatSourceId == s.Id))
            .Select(s => (int?)s.CatalogSourceId)
            .FirstOrDefaultAsync(cancellationToken);

        if (catalogSourceId is null)
        {
            return;
        }

        // Explicit source deletion ends its subscription and releases the retained
        // candidate reference for normal CAS garbage collection.
        await _context.DatSubscriptions.Where(s => s.DatSourceId == datSourceId)
            .ExecuteDeleteAsync(cancellationToken);

        // DatSource first (it restricts CatalogSource deletion), then the catalog source,
        // which cascades entries and links. No games remain to trip the entry Restrict:
        // an orphaned source has no versions, and games cascade with their version.
        await _context.DatSources
            .Where(s => s.Id == datSourceId)
            .ExecuteDeleteAsync(cancellationToken);

        await _context.CatalogSources
            .Where(c => c.Id == catalogSourceId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> GetCatalogSourceIdAsync(int datSourceId, CancellationToken cancellationToken = default)
    {
        return await _context.DatSources
            .Where(s => s.Id == datSourceId)
            .Select(s => s.CatalogSourceId)
            .SingleAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> AddGamesBatchAsync(
        IReadOnlyList<DatGameWithEntry> games,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0)
        {
            return Array.Empty<int>();
        }

        // Entry identity is catalog-owned: callers obtain SourceEntryIds from the title
        // derivation service and stitch them onto their payload rows here.
        var entities = new List<DatGameEntity>(games.Count);
        foreach (var game in games)
        {
            var entity = DatGameEntity.FromDomain(game.Game);
            entity.SourceEntryId = game.SourceEntryId;
            entities.Add(entity);
        }

        _context.DatGames.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);

        // Capture assigned IDs before clearing tracker
        var ids = entities.Select(e => e.Id).ToList();

        // Clear change tracker to prevent memory buildup
        _context.ChangeTracker.Clear();

        return ids;
    }

    public async Task AddGameRegionsBatchAsync(IReadOnlyList<(int GameId, int RegionId)> mappings, CancellationToken cancellationToken = default)
    {
        if (mappings.Count == 0) return;

        var entities = mappings.Select(m => new DatGameRegionEntity
        {
            DatGameId = m.GameId,
            RegionId = m.RegionId
        }).ToList();

        _context.DatGameRegions.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
        _context.ChangeTracker.Clear();
    }

    public async Task AddGameLanguagesBatchAsync(IReadOnlyList<(int GameId, int LanguageId)> mappings, CancellationToken cancellationToken = default)
    {
        if (mappings.Count == 0) return;

        var entities = mappings.Select(m => new DatGameLanguageEntity
        {
            DatGameId = m.GameId,
            GameLanguageId = m.LanguageId
        }).ToList();

        _context.DatGameLanguages.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
        _context.ChangeTracker.Clear();
    }

    public async Task UpdateCountsAsync(int datFileId, int gameCount, int romCount, int diskCount,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // ExecuteUpdateAsync works directly on entity properties - no mapping needed
        await _context.DatFiles
            .Where(d => d.Id == datFileId)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.GameCount, gameCount)
                    .SetProperty(d => d.RomCount, romCount)
                    .SetProperty(d => d.DiskCount, diskCount)
                    .SetProperty(d => d.UpdatedAt, now),
                cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await _context.DatFiles
            .Where(d => d.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        await PruneUnreferencedDatSourceEntriesAsync(cancellationToken);
    }

    public async Task AcquireMutationWriteLockAsync(
        int datFileId,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "DAT mutation write-lock acquisition requires a caller-owned transaction.");
        }

        // One transaction-scoped advisory lock serializes every catalog-topology writer
        // (DAT delete/replace, title moves, derivation, and source-link writers) so the
        // read-before-delete state derived after this call is exactly the state the mutation
        // consumes. PostgreSQL row locks cannot provide that boundary across tables.
        await CatalogTopologyFence.AcquireAsync(_context, cancellationToken);
    }


    public async Task<DatRom?> FindRomBySha1Async(Sha1 sha1, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DatRoms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Sha1 == sha1, cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<DatRom>> FindRomsBySha1Async(Sha1 sha1,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.DatRoms
            .AsNoTracking()
            .Where(r => r.Sha1 == sha1)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<PagedList<DatGame>> GetGamesByDatIdAsync(
        int datId,
        string? cursor,
        int limit = 50,
        BiosFilter biosFilter = BiosFilter.Exclude,
        int? libraryId = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Decode Cursor
        var cursorData = CursorUtils.FromCursor<CursorData<string>>(cursor);

        // 2. Base Query
        var query = _context.DatGames
            .AsNoTracking()
            .Where(g => g.DatFileId == datId);

        // 3. Apply BIOS Filter
        query = biosFilter switch
        {
            BiosFilter.Exclude => query.Where(g => !g.IsBios),
            BiosFilter.Only => query.Where(g => g.IsBios),
            _ => query // Include: no filter
        };

        // 4. Apply Library Filter via resolver-recommended release projection rows.
        query = query.ApplyLibrary(libraryId, _context.MaterializedLibraryReleases, _context.Libraries);

        // 5. Apply Cursor Filter (The "Seek")
        if (cursorData is not null)
        {
            string sortName = cursorData.SortValue;

            // SQL Translation:
            // WHERE (Name > @sortName) OR (Name = @sortName AND Id > @lastId)
            query = query.Where(g =>
                g.Name.CompareTo(sortName) > 0 ||
                (g.Name == sortName && g.Id > cursorData.Id));
        }

        // 6. Execution (Fetch limit + 1 to detect if there's a next page)
        var entities = await query
            .OrderBy(g => g.Name)
            .ThenBy(g => g.Id)
            .Take(limit + 1)
            .Include(g => g.Roms)
            .Include(g => g.Disks)
            .ToListAsync(cancellationToken);

        // 6. Calculate Next Cursor
        string? nextCursor = null;
        bool hasNextPage = entities.Count > limit;

        if (hasNextPage)
        {
            // Get cursor from last valid item (before the +1 overflow)
            var lastItem = entities[limit - 1];
            nextCursor = CursorUtils.ToCursor(new CursorData<string>(lastItem.Name, lastItem.Id));

            // Remove the "peek" item so we only return the requested amount
            entities.RemoveAt(limit);
        }

        // Map entities to domain models
        var domainItems = entities.Select(e => e.ToDomain()).ToList();

        return new PagedList<DatGame>(domainItems, nextCursor, hasNextPage);
    }

    public async Task<PagedList<DatRom>> GetRomsByGameIdAsync(
        int gameId,
        string? cursor,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var cursorData = CursorUtils.FromCursor<CursorData<string>>(cursor);

        var query = _context.DatRoms
            .AsNoTracking()
            .Where(r => r.DatGameId == gameId);

        if (cursorData is not null)
        {
            string sortName = cursorData.SortValue;

            query = query.Where(r =>
                r.Name.CompareTo(sortName) > 0 ||
                (r.Name == sortName && r.Id > cursorData.Id));
        }

        var entities = await query
            .OrderBy(r => r.Name)
            .ThenBy(r => r.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        bool hasNextPage = entities.Count > limit;

        if (hasNextPage)
        {
            var lastItem = entities[limit - 1];
            nextCursor = CursorUtils.ToCursor(new CursorData<string>(lastItem.Name, lastItem.Id));
            entities.RemoveAt(limit);
        }

        var domainItems = entities.Select(e => e.ToDomain()).ToList();
        return new PagedList<DatRom>(domainItems, nextCursor, hasNextPage);
    }

    public async Task<PagedList<DatRom>> GetRomsByDatIdAsync(
        int datId,
        string? cursor,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var cursorData = CursorUtils.FromCursor<CursorData<string>>(cursor);

        var query = _context.DatRoms
            .AsNoTracking()
            .Join(
                _context.DatGames,
                rom => rom.DatGameId,
                game => game.Id,
                (rom, game) => new { Rom = rom, game.DatFileId })
            .Where(x => x.DatFileId == datId)
            .Select(x => x.Rom);

        if (cursorData is not null)
        {
            string sortName = cursorData.SortValue;

            query = query.Where(r =>
                r.Name.CompareTo(sortName) > 0 ||
                (r.Name == sortName && r.Id > cursorData.Id));
        }

        var entities = await query
            .OrderBy(r => r.Name)
            .ThenBy(r => r.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        bool hasNextPage = entities.Count > limit;

        if (hasNextPage)
        {
            var lastItem = entities[limit - 1];
            nextCursor = CursorUtils.ToCursor(new CursorData<string>(lastItem.Name, lastItem.Id));
            entities.RemoveAt(limit);
        }

        var domainItems = entities.Select(e => e.ToDomain()).ToList();
        return new PagedList<DatRom>(domainItems, nextCursor, hasNextPage);
    }

    public async Task<DatGame?> GetGameByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DatGames
            .AsNoTracking()
            .Include(g => g.Roms)
            .Include(g => g.Disks)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<int> LinkDatRomsToRomFileAsync(Sha1 sha1, int romFileId,
        CancellationToken cancellationToken = default)
    {
        // Bulk update all DatRoms matching the SHA1 to link to the RomFile
        return await _context.DatRoms
            .Where(r => r.Sha1 == sha1 && r.RomFileId != romFileId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.RomFileId, romFileId),
                cancellationToken);
    }

    public async Task<int> LinkDatRomsToExistingRomFilesAsync(
        IReadOnlyList<int> datGameIds,
        CancellationToken cancellationToken = default)
    {
        if (datGameIds.Count == 0)
        {
            return 0;
        }

        var matches = await (
                from datRom in _context.DatRoms.AsNoTracking()
                where datGameIds.Contains(datRom.DatGameId)
                      && datRom.RomFileId == null
                      && datRom.Sha1 != null
                join romFile in _context.RomFiles.AsNoTracking()
                    on datRom.Sha1 equals romFile.Sha1
                select new { DatRomId = datRom.Id, RomFileId = romFile.Id })
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
        {
            return 0;
        }

        var romFileIdsByDatRomId = matches.ToDictionary(match => match.DatRomId, match => match.RomFileId);
        var matchedDatRomIds = romFileIdsByDatRomId.Keys.ToList();
        var datRoms = await _context.DatRoms
            .AsTracking()
            .Where(datRom => matchedDatRomIds.Contains(datRom.Id))
            .ToListAsync(cancellationToken);

        foreach (var datRom in datRoms)
        {
            datRom.RomFileId = romFileIdsByDatRomId[datRom.Id];
        }

        await _context.SaveChangesAsync(cancellationToken);
        _context.ChangeTracker.Clear();

        return datRoms.Count;
    }

    public async Task<IReadOnlyList<DatGame>> GetAllGamesByDatIdAsync(
        int datId,
        BiosFilter biosFilter = BiosFilter.Exclude,
        CancellationToken cancellationToken = default)
    {
        // Get all games for a DAT without pagination (for platform assignment)
        // Does not include ROMs/Disks for efficiency
        var query = _context.DatGames
            .AsNoTracking()
            .Where(g => g.DatFileId == datId);

        // Apply BIOS filter
        query = biosFilter switch
        {
            BiosFilter.Exclude => query.Where(g => !g.IsBios),
            BiosFilter.Only => query.Where(g => g.IsBios),
            _ => query // Include: no filter
        };

        var entities = await query
            .OrderBy(g => g.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    // Id translation note: until the derivation facade re-points callers, ids crossing the
    // ITitleSourceAssignmentStore boundary are DAT game ids (the public API still speaks
    // them). This adapter translates game id <-> SourceEntryId in both directions; links
    // themselves are keyed by SourceEntryId only.

    public Task UpsertAssignmentsAsync(
        IReadOnlyList<TitleSourceAssignment> assignments,
        CancellationToken cancellationToken = default) =>
        WriteTitleSourceLinksAsync(assignments, cancellationToken);

    private async Task WriteTitleSourceLinksAsync(
        IReadOnlyList<TitleSourceAssignment> assignments,
        CancellationToken cancellationToken)
    {
        if (assignments.Count == 0)
        {
            return;
        }

        var gameIds = assignments.Select(a => a.SourceEntryId).ToList();
        var entryIdByGameId = await _context.DatGames
            .Where(g => gameIds.Contains(g.Id))
            .Select(g => new { g.Id, g.SourceEntryId })
            .ToDictionaryAsync(g => g.Id, g => g.SourceEntryId, cancellationToken);

        // Games from successive DAT versions share an entry; last assignment wins in-batch.
        var titleIdByEntryId = new Dictionary<int, int>();
        foreach (var assignment in assignments)
        {
            if (entryIdByGameId.TryGetValue(assignment.SourceEntryId, out int entryId))
            {
                titleIdByEntryId[entryId] = assignment.TitleId;
            }
        }

        var entryIds = titleIdByEntryId.Keys.ToList();
        var existing = await _context.TitleSourceLinks
            .AsTracking()
            .Where(l => entryIds.Contains(l.SourceEntryId))
            .ToDictionaryAsync(l => l.SourceEntryId, cancellationToken);

        foreach (var (entryId, titleId) in titleIdByEntryId)
        {
            if (existing.TryGetValue(entryId, out var link))
            {
                // Curation-path write: overwrites deliberately. Derivation writes go through
                // ITitleDerivationService, which never overwrites existing links.
                link.TitleId = titleId;
            }
            else
            {
                _context.TitleSourceLinks.Add(new TitleSourceLinkEntity
                {
                    SourceEntryId = entryId,
                    TitleId = titleId
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        _context.ChangeTracker.Clear();
    }

    public async Task UpdateDatFilePlatformAsync(int datFileId, int platformId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        await _context.DatFiles
            .Where(d => d.Id == datFileId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(d => d.PlatformId, platformId)
                    .SetProperty(d => d.UpdatedAt, now),
                cancellationToken);

        // Routing stamps the platform onto the version's neutral entries, which were created
        // with a null platform while the source was unrouted.
        await _context.SourceEntries
            .Where(e => _context.DatGames.Any(g => g.SourceEntryId == e.Id && g.DatFileId == datFileId))
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.PlatformId, platformId),
                cancellationToken);
    }

    public async Task<int> BackfillIsBiosAsync(CancellationToken cancellationToken = default)
    {
        // Mark games as BIOS if:
        // - Category equals "BIOS" (exact match, not contains, to avoid false positives)
        // - Name contains "[BIOS]" (bracket notation is specific to BIOS naming convention)
        return await _context.DatGames
            .Where(g => !g.IsBios)
            .Where(g =>
                g.Category == "BIOS" ||
                g.Name.Contains("[BIOS]"))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.IsBios, true), cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetSourceEntryIdsByTitleAsync(int titleId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TitleSourceLinks
            .Where(l => l.TitleId == titleId)
            .Join(
                _context.DatGames,
                l => l.SourceEntryId,
                g => g.SourceEntryId,
                (_, g) => g.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<TitleSourceAssignmentContext?> GetAssignmentContextAsync(int sourceEntryId,
        CancellationToken cancellationToken = default)
    {
        var result = await _context.DatGames
            .AsNoTracking()
            .Where(g => g.Id == sourceEntryId)
            .Select(g => new
            {
                PlatformId = _context.DatFiles
                    .Where(d => d.Id == g.DatFileId)
                    .Select(d => d.PlatformId)
                    .FirstOrDefault(),
                TitleId = _context.TitleSourceLinks
                    .Where(l => l.SourceEntryId == g.SourceEntryId)
                    .Select(l => (int?)l.TitleId)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (result is null)
        {
            return null;
        }

        return new TitleSourceAssignmentContext(sourceEntryId, result.PlatformId, result.TitleId);
    }

    public async Task ClearAssignmentsAsync(IReadOnlyList<int> sourceEntryIds,
        CancellationToken cancellationToken = default)
    {
        if (sourceEntryIds.Count == 0)
        {
            return;
        }

        await _context.TitleSourceLinks
            .Where(l => _context.DatGames.Any(
                g => sourceEntryIds.Contains(g.Id) && g.SourceEntryId == l.SourceEntryId))
            .ExecuteDeleteAsync(cancellationToken);
    }

}
