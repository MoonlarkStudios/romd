using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

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

    public async Task<Region> AddAsync(Region region, CancellationToken ct = default)
    {
        var entity = RegionEntity.FromDomain(region);
        _context.Regions.Add(entity);
        await _context.SaveChangesAsync(ct);
        return entity.ToDomain();
    }

    public async Task AddRangeAsync(IReadOnlyList<Region> regions, CancellationToken ct = default)
    {
        if (regions.Count == 0) return;

        var entities = regions.Select(RegionEntity.FromDomain).ToList();
        _context.Regions.AddRange(entities);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _context.Regions
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(ct);
    }

    public async Task AddAliasAsync(int entityId, string normalizedAlias, CancellationToken ct = default)
    {
        _context.RegionAliases.Add(new RegionAliasEntity
        {
            RegionId = entityId,
            NormalizedAlias = normalizedAlias
        });
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddAliasesBatchAsync(int entityId, IReadOnlyList<string> normalizedAliases, CancellationToken ct = default)
    {
        if (normalizedAliases.Count == 0) return;

        var entities = normalizedAliases.Select(alias => new RegionAliasEntity
        {
            RegionId = entityId,
            NormalizedAlias = alias
        }).ToList();

        _context.RegionAliases.AddRange(entities);
        await _context.SaveChangesAsync(ct);
        _context.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<(int Id, string Alias)>> GetAliasesAsync(int entityId, CancellationToken ct = default)
    {
        // Tuples are built client-side: Npgsql translates ValueTuple.Create into a PostgreSQL
        // record, which it cannot read back as a .NET tuple.
        var aliases = await _context.RegionAliases
            .AsNoTracking()
            .Where(a => a.RegionId == entityId)
            .Select(a => new { a.Id, a.NormalizedAlias })
            .ToListAsync(ct);
        return aliases.Select(a => (a.Id, a.NormalizedAlias)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetAllAliasesAsync(CancellationToken ct = default)
    {
        var aliases = await _context.RegionAliases
            .AsNoTracking()
            .Select(a => new { a.NormalizedAlias, a.RegionId })
            .ToListAsync(ct);

        return aliases.ToDictionary(a => a.NormalizedAlias, a => a.RegionId);
    }

    public async Task<bool> AliasExistsAsync(string normalizedAlias, CancellationToken ct = default) =>
        await _context.RegionAliases.AnyAsync(a => a.NormalizedAlias == normalizedAlias, ct);

    public async Task RemoveAliasAsync(int aliasId, CancellationToken ct = default)
    {
        var alias = await _context.RegionAliases.AsTracking().SingleOrDefaultAsync(item => item.Id == aliasId, ct);
        if (alias is null) return;
        _context.RegionAliases.Remove(alias);
    }

    public Task<bool> HasReferenceIdentityAsync(int id, CancellationToken ct = default) =>
        _context.Regions.AnyAsync(entity => entity.Id == id && entity.CanonicalKey != null, ct);

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
        _context.AdminAuditEvents.Add(new AdminAuditEventEntity
        {
            TargetType = "Region", TargetId = sourceId.ToString(), Action = "Merged",
            Changes = System.Text.Json.JsonSerializer.Serialize(new { TargetId = targetId })
        });
    }

    public async Task<IReadOnlyList<(Region Entity, bool CanMerge, IReadOnlyList<(int Id, string Alias, string Ownership)> Aliases)>>
        GetAllWithAliasesAsync(CancellationToken ct = default)
    {
        var regions = await _context.Regions
            .AsNoTracking()
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

        var allAliases = await _context.RegionAliases
            .AsNoTracking()
            .ToListAsync(ct);

        var aliasesByRegion = allAliases
            .GroupBy(a => a.RegionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(int Id, string Alias, string Ownership)>)
                g.Select(a => (a.Id, a.NormalizedAlias, a.Ownership?.ToString() ?? "Unresolved")).ToList());

        return regions.Select(r => (
            r.ToDomain(), r.CanonicalKey == null,
            aliasesByRegion.GetValueOrDefault(r.Id, [])
        )).ToList();
    }
}
