using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Management.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;

using Region = Romd.Domain.Taxonomy.Region;

namespace Romd.Persistence.Repositories;

public sealed class RegionRepository : IRegionRepository
{
    private readonly RomdDbContext _context;

    public RegionRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<Region?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var entity = await _context.Regions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<Region>> GetAllAsync(CancellationToken ct = default)
    {
        var entities = await _context.Regions
            .AsNoTracking()
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default) =>
        await _context.Regions.AnyAsync(ct);

    public async Task<IStagedTaxonomyRow> AddStagedAsync(Region region, string normalizedToken, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        TaxonomyStagingReceipts.RequireTransaction(_context);
        if (region.Id != 0)
            throw new ArgumentException("Stage only a new identity-free taxonomy row.", nameof(region));
        // Minting is serialized across processes until the caller's transaction ends, so the free candidate
        // read here is still free at commit; the unique key index is the backstop. Resolver callers already
        // hold the lock, so this reentrant take only guards one that does not.
        await LockMintingAsync(ct);
        string preferred = UnregisteredTaxonomyKey.Candidates(normalizedToken).First();
        string stem = UnregisteredTaxonomyKey.HashedStem(normalizedToken);
        var taken = await _context.Regions
            .Where(r => r.CanonicalKey == preferred || r.CanonicalKey == stem || r.CanonicalKey.StartsWith(stem + "-"))
            .Select(r => r.CanonicalKey)
            .ToListAsync(ct);
        var entity = RegionEntity.FromDomain(region, UnregisteredTaxonomyKey.Mint(normalizedToken, taken.Contains));
        _context.Regions.Add(entity);
        return TaxonomyStagingReceipts.Row(_context, entity);
    }

    public Task<int?> ExecuteMintSavepointAsync(Func<CancellationToken, Task<int?>> operation, CancellationToken ct = default) =>
        TaxonomyEntryProjection.MintAsync(_context, operation, ct);

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _context.Regions
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(ct);
    }

    public Task<int?> GetIdByKeyAsync(string key, CancellationToken ct = default) =>
        _context.Regions.Where(r => r.CanonicalKey == key).Select(r => (int?)r.Id).SingleOrDefaultAsync(ct);

    public Task<IStagedTaxonomyAlias> AddAliasStagedAsync(int entityId, string normalizedAlias, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        TaxonomyStagingReceipts.RequireTransaction(_context);
        var alias = _context.RegionAliases.Add(new RegionAliasEntity { RegionId = entityId, NormalizedAlias = normalizedAlias }).Entity;
        return Task.FromResult(TaxonomyStagingReceipts.Alias(_context, alias,
            () => new TaxonomyAlias(alias.Id, alias.NormalizedAlias, TaxonomyEntryProjection.AliasOwnership(alias.Ownership))));
    }

    public bool IsAliasConflict(Exception error) =>
        error is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };

    // Executed as written: composing over the locking SELECT would wrap it in a subquery.
    public async Task<int?> LockByAliasAsync(string normalizedAlias, CancellationToken ct = default) =>
        (await _context.Database.SqlQuery<int>($"""
            SELECT r."Id" AS "Value" FROM romd."Regions" r
            JOIN romd."RegionAliases" a ON a."RegionId" = r."Id"
            WHERE a."NormalizedAlias" = {normalizedAlias}
            FOR KEY SHARE OF r
            """).ToListAsync(ct)) is [var id] ? id : null;

    public async Task<bool> AliasExistsAsync(string normalizedAlias, CancellationToken ct = default) =>
        await _context.RegionAliases.AnyAsync(a => a.NormalizedAlias == normalizedAlias, ct);

    public async Task<bool> RemoveAliasAsync(int entityId, int aliasId, CancellationToken ct = default)
    {
        var alias = await _context.RegionAliases.AsTracking()
            .SingleOrDefaultAsync(item => item.Id == aliasId && item.RegionId == entityId, ct);
        if (alias is null)
            return false;
        _context.RegionAliases.Remove(alias);
        return true;
    }

    public Task<bool> HasReferenceIdentityAsync(int id, CancellationToken ct = default) =>
        _context.Regions.AnyAsync(entity => entity.Id == id && entity.Ownership != null, ct);

    public Task LockMintingAsync(CancellationToken ct = default) => TaxonomyEntryProjection.LockMintingAsync(_context, ct);

    public Task LimitLockWaitsAsync(TimeSpan timeout, CancellationToken ct = default) =>
        TaxonomyEntryProjection.LimitLockWaitsAsync(_context, timeout, ct);

    public Task<IReadOnlySet<int>> LockAsync(IReadOnlyCollection<(int Id, TaxonomyRowLock Mode)> rows, CancellationToken ct = default) =>
        TaxonomyEntryProjection.LockRowsAsync(_context, rows, (id, mode) => mode switch
        {
            TaxonomyRowLock.KeyShare => $"""SELECT "Id" AS "Value" FROM romd."Regions" WHERE "Id" = {id} FOR KEY SHARE""",
            TaxonomyRowLock.NoKeyUpdate => $"""SELECT "Id" AS "Value" FROM romd."Regions" WHERE "Id" = {id} FOR NO KEY UPDATE""",
            TaxonomyRowLock.Update => $"""SELECT "Id" AS "Value" FROM romd."Regions" WHERE "Id" = {id} FOR UPDATE""",
            _ => throw new ArgumentOutOfRangeException(nameof(rows), mode, "Unknown row lock mode.")
        }, ct);

    public async Task ReassignAliasesAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        await _context.RegionAliases
            .Where(a => a.RegionId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.RegionId, targetId), ct);
    }

    public async Task ReassignJunctionsAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        // Delete junction rows where target already exists (avoid duplicate composite keys)
        await _context.DatGameRegions
            .Where(dgr => dgr.RegionId == sourceId &&
                           _context.DatGameRegions.Any(existing =>
                               existing.DatGameId == dgr.DatGameId && existing.RegionId == targetId))
            .ExecuteDeleteAsync(ct);

        // Update remaining source rows to target
        await _context.DatGameRegions
            .Where(dgr => dgr.RegionId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(dgr => dgr.RegionId, targetId), ct);
        // Canonical releases must retain their taxonomy when the source value is removed.
        await _context.CatalogReleaseRegions.Where(item => item.RegionId == sourceId &&
            _context.CatalogReleaseRegions.Any(other => other.CatalogReleaseId == item.CatalogReleaseId && other.RegionId == targetId))
            .ExecuteDeleteAsync(ct);
        await _context.CatalogReleaseRegions.Where(item => item.RegionId == sourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RegionId, targetId), ct);
        // The audit names public keys, never internal ids.
        var keys = await _context.Regions.Where(r => r.Id == sourceId || r.Id == targetId)
            .ToDictionaryAsync(r => r.Id, r => r.CanonicalKey, ct);
        _context.AdminAuditEvents.Add(new AdminAuditEventEntity
        {
            TargetType = "Region",
            TargetId = keys[sourceId],
            Action = "Merged",
            Changes = System.Text.Json.JsonSerializer.Serialize(new { TargetKey = keys[targetId] })
        });
    }

    public async Task<TaxonomyMergeImpact> GetMergeImpactAsync(int sourceId, CancellationToken ct = default) =>
        new(await _context.RegionAliases.CountAsync(item => item.RegionId == sourceId, ct),
            await _context.DatGameRegions.CountAsync(item => item.RegionId == sourceId, ct),
            await _context.CatalogReleaseRegions.CountAsync(item => item.RegionId == sourceId, ct));

    public async Task<PagedList<TaxonomyEntry>> ListWithAliasesAsync(TaxonomyPageRequest request, CancellationToken ct = default)
    {
        var rows = _context.Regions.AsNoTracking();
        if (request.Query.Length > 0)
            rows = rows.Where(r => ReferenceSearch.Normalize(r.Name).Contains(request.Query)
                || ReferenceSearch.Normalize(r.CanonicalKey).Contains(request.Query)
                || _context.RegionAliases.Any(a => a.RegionId == r.Id && ReferenceSearch.Normalize(a.NormalizedAlias).Contains(request.Query)));
        if (request is { AfterSortOrder: { } sortOrder, AfterName: { } name, AfterKey: { } key })
            rows = rows.Where(r => r.SortOrder > sortOrder || (r.SortOrder == sortOrder &&
                (string.Compare(r.Name, name) > 0 || (r.Name == name && string.Compare(r.CanonicalKey, key) > 0))));
        var page = await rows.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ThenBy(r => r.CanonicalKey)
            .Take(request.Limit + 1)
            .ToListAsync(ct);
        bool hasNext = page.Count > request.Limit;
        var items = await DescribeAsync(page.Take(request.Limit).ToList(), ct);
        return new PagedList<TaxonomyEntry>(items,
            hasNext ? request.CursorFor(items[^1].SortOrder, items[^1].Name, items[^1].Key) : null, hasNext);
    }

    public async Task<TaxonomyEntry?> GetWithAliasesAsync(string key, CancellationToken ct = default) =>
        await _context.Regions.AsNoTracking().SingleOrDefaultAsync(r => r.CanonicalKey == key, ct) is { } row
            ? (await DescribeAsync([row], ct))[0]
            : null;

    public async Task<PagedList<TaxonomyAlias>> ListAliasesAsync(int entityId, TaxonomyAliasPageRequest request, CancellationToken ct = default)
    {
        var aliases = _context.RegionAliases.AsNoTracking().Where(a => a.RegionId == entityId);
        if (request.AfterId is { } afterId)
            aliases = aliases.Where(a => a.Id > afterId);
        var page = await aliases.OrderBy(a => a.Id).Take(request.Limit + 1).ToListAsync(ct);
        return TaxonomyEntryProjection.AliasPage(
            page.Select(a => new TaxonomyAlias(a.Id, a.NormalizedAlias, TaxonomyEntryProjection.AliasOwnership(a.Ownership))).ToList(), request);
    }

    // The compatibility columns hold a registered row's effective facts (publication refreshes them) and an
    // unregistered row's operational facts, so one projection serves both kinds.
    private async Task<IReadOnlyList<TaxonomyEntry>> DescribeAsync(IReadOnlyList<RegionEntity> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToArray();
        // Each row's first aliases and one more, which shows whether its alias collection continues. The aliases are
        // narrowed to these rows before they are numbered per row, so the read never ranges over other rows' aliases.
        var aliases = (await _context.Regions.AsNoTracking()
                .Where(r => ids.Contains(r.Id))
                .SelectMany(r => _context.RegionAliases.AsNoTracking().Where(a => ids.Contains(a.RegionId) && a.RegionId == r.Id)
                    .OrderBy(a => a.Id).Take(TaxonomyAliasPageRequest.EmbeddedLimit + 1))
                .ToListAsync(ct))
            .ToLookup(a => a.RegionId, a => new TaxonomyAlias(a.Id, a.NormalizedAlias, TaxonomyEntryProjection.AliasOwnership(a.Ownership)));
        return rows.Select(row => TaxonomyEntryProjection.Versioned(new TaxonomyEntry(row.CanonicalKey, row.Ownership?.ToString(), row.BuiltInVersion,
            row.Name, row.BaseDescription, row.SortOrder, row.Retired, row.IsAutoCreated, null, TaxonomyEntryProjection.FirstPage(aliases[row.Id]), "")))
            .ToArray();
    }
}
