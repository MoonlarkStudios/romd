using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class LanguageRepository : ILanguageRepository
{
    private readonly RomdDbContext _context;

    public LanguageRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<GameLanguage?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var entity = await _context.GameLanguages
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<GameLanguage>> GetAllAsync(CancellationToken ct = default)
    {
        var entities = await _context.GameLanguages
            .AsNoTracking()
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default) =>
        await _context.GameLanguages.AnyAsync(ct);

    public async Task<GameLanguage> AddAsync(GameLanguage language, CancellationToken ct = default)
    {
        var entity = GameLanguageEntity.FromDomain(language);
        _context.GameLanguages.Add(entity);
        await _context.SaveChangesAsync(ct);
        return entity.ToDomain();
    }

    public async Task AddRangeAsync(IReadOnlyList<GameLanguage> languages, CancellationToken ct = default)
    {
        if (languages.Count == 0) return;

        var entities = languages.Select(GameLanguageEntity.FromDomain).ToList();
        _context.GameLanguages.AddRange(entities);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _context.GameLanguages
            .Where(l => l.Id == id)
            .ExecuteDeleteAsync(ct);
    }

    public async Task AddAliasAsync(int entityId, string normalizedAlias, CancellationToken ct = default)
    {
        _context.GameLanguageAliases.Add(new GameLanguageAliasEntity
        {
            GameLanguageId = entityId,
            NormalizedAlias = normalizedAlias
        });
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddAliasesBatchAsync(int entityId, IReadOnlyList<string> normalizedAliases, CancellationToken ct = default)
    {
        if (normalizedAliases.Count == 0) return;

        var entities = normalizedAliases.Select(alias => new GameLanguageAliasEntity
        {
            GameLanguageId = entityId,
            NormalizedAlias = alias
        }).ToList();

        _context.GameLanguageAliases.AddRange(entities);
        await _context.SaveChangesAsync(ct);
        _context.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<(int Id, string Alias)>> GetAliasesAsync(int entityId, CancellationToken ct = default)
    {
        // Tuples are built client-side: Npgsql translates ValueTuple.Create into a PostgreSQL
        // record, which it cannot read back as a .NET tuple.
        var aliases = await _context.GameLanguageAliases
            .AsNoTracking()
            .Where(a => a.GameLanguageId == entityId)
            .Select(a => new { a.Id, a.NormalizedAlias })
            .ToListAsync(ct);
        return aliases.Select(a => (a.Id, a.NormalizedAlias)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetAllAliasesAsync(CancellationToken ct = default)
    {
        var aliases = await _context.GameLanguageAliases
            .AsNoTracking()
            .Select(a => new { a.NormalizedAlias, a.GameLanguageId })
            .ToListAsync(ct);

        return aliases.ToDictionary(a => a.NormalizedAlias, a => a.GameLanguageId);
    }

    public async Task<bool> AliasExistsAsync(string normalizedAlias, CancellationToken ct = default) =>
        await _context.GameLanguageAliases.AnyAsync(a => a.NormalizedAlias == normalizedAlias, ct);

    public async Task RemoveAliasAsync(int aliasId, CancellationToken ct = default)
    {
        var alias = await _context.GameLanguageAliases.AsTracking().SingleOrDefaultAsync(item => item.Id == aliasId, ct);
        if (alias is null) return;
        _context.GameLanguageAliases.Remove(alias);
    }

    public Task<bool> HasReferenceIdentityAsync(int id, CancellationToken ct = default) =>
        _context.GameLanguages.AnyAsync(entity => entity.Id == id && entity.CanonicalKey != null, ct);

    public async Task ReassignAliasesAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        await _context.GameLanguageAliases
            .Where(a => a.GameLanguageId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.GameLanguageId, targetId), ct);
    }

    public async Task ReassignJunctionsAsync(int sourceId, int targetId, CancellationToken ct = default)
    {
        // Delete junction rows where target already exists (avoid duplicate composite keys)
        await _context.DatGameLanguages
            .Where(dgl => dgl.GameLanguageId == sourceId &&
                           _context.DatGameLanguages.Any(existing =>
                               existing.DatGameId == dgl.DatGameId && existing.GameLanguageId == targetId))
            .ExecuteDeleteAsync(ct);

        // Update remaining source rows to target
        await _context.DatGameLanguages
            .Where(dgl => dgl.GameLanguageId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(dgl => dgl.GameLanguageId, targetId), ct);
        // Canonical releases must retain their taxonomy when the source value is removed.
        await _context.CatalogReleaseLanguages.Where(item => item.GameLanguageId == sourceId &&
            _context.CatalogReleaseLanguages.Any(other => other.CatalogReleaseId == item.CatalogReleaseId && other.GameLanguageId == targetId))
            .ExecuteDeleteAsync(ct);
        await _context.CatalogReleaseLanguages.Where(item => item.GameLanguageId == sourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GameLanguageId, targetId), ct);
        _context.AdminAuditEvents.Add(new AdminAuditEventEntity
        {
            TargetType = "Language", TargetId = sourceId.ToString(), Action = "Merged",
            Changes = System.Text.Json.JsonSerializer.Serialize(new { TargetId = targetId })
        });
    }

    public async Task<IReadOnlyList<(GameLanguage Entity, bool CanMerge, IReadOnlyList<(int Id, string Alias, string Ownership)> Aliases)>>
        GetAllWithAliasesAsync(CancellationToken ct = default)
    {
        var languages = await _context.GameLanguages
            .AsNoTracking()
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync(ct);

        var allAliases = await _context.GameLanguageAliases
            .AsNoTracking()
            .ToListAsync(ct);

        var aliasesByLanguage = allAliases
            .GroupBy(a => a.GameLanguageId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(int Id, string Alias, string Ownership)>)
                g.Select(a => (a.Id, a.NormalizedAlias, a.Ownership?.ToString() ?? "Unresolved")).ToList());

        return languages.Select(l => (
            l.ToDomain(), l.CanonicalKey == null,
            aliasesByLanguage.GetValueOrDefault(l.Id, [])
        )).ToList();
    }
}
