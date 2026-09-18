using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Source.Platform;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class PlatformAliasRepository : IPlatformAliasRepository
{
    private readonly RomdDbContext _context;

    public PlatformAliasRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<PlatformAlias>> GetByPlatformIdAsync(
        int platformId,
        CancellationToken ct = default)
    {
        var entities = await _context.PlatformAliases
            .AsNoTracking()
            .Where(a => a.PlatformId == platformId)
            .OrderBy(a => a.Type)
            .ThenBy(a => a.Value)
            .ToListAsync(ct);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<PlatformAlias?> GetByIdAsync(int aliasId, CancellationToken ct = default)
    {
        var entity = await _context.PlatformAliases
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == aliasId, ct);

        return entity?.ToDomain();
    }

    public async Task<PlatformAlias> AddAsync(PlatformAlias alias, CancellationToken ct = default)
    {
        var entity = PlatformAliasEntity.FromDomain(alias);
        _context.PlatformAliases.Add(entity);
        await _context.SaveChangesAsync(ct);
        return entity.ToDomain();
    }

    public async Task AddRangeAsync(IReadOnlyList<PlatformAlias> aliases, CancellationToken ct = default)
    {
        if (aliases.Count == 0)
        {
            return;
        }

        var entities = aliases.Select(PlatformAliasEntity.FromDomain).ToList();
        _context.PlatformAliases.AddRange(entities);
        await _context.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(int aliasId, CancellationToken ct = default)
    {
        await _context.PlatformAliases
            .Where(a => a.Id == aliasId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<bool> NameAliasExistsAsync(string normalizedValue, CancellationToken ct = default) =>
        await _context.PlatformAliases
            .AnyAsync(a => a.Type == PlatformAliasType.Name && a.NormalizedValue == normalizedValue, ct);

    public async Task<bool> ProviderMappingExistsAsync(
        int platformId,
        string provider,
        CancellationToken ct = default) =>
        await _context.PlatformAliases
            .AnyAsync(
                a => a.Type == PlatformAliasType.ProviderMapping
                     && a.PlatformId == platformId
                     && a.Provider == provider,
                ct);

    public async Task<bool> AnyProviderMappingsAsync(string provider, CancellationToken ct = default) =>
        await _context.PlatformAliases
            .AnyAsync(a => a.Type == PlatformAliasType.ProviderMapping && a.Provider == provider, ct);

    public async Task<bool> AnyNameAliasesAsync(CancellationToken ct = default) =>
        await _context.PlatformAliases
            .AnyAsync(a => a.Type == PlatformAliasType.Name, ct);

    public async Task<IReadOnlyList<(int PlatformId, string NormalizedValue)>> GetAllNameAliasesAsync(
        CancellationToken ct = default)
    {
        var rows = await _context.PlatformAliases
            .AsNoTracking()
            .Where(a => a.Type == PlatformAliasType.Name)
            .Select(a => new { a.PlatformId, a.NormalizedValue })
            .ToListAsync(ct);

        return rows.Select(r => (r.PlatformId, r.NormalizedValue)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetProviderMappingsByShortNameAsync(
        string provider,
        CancellationToken ct = default)
    {
        var rows = await _context.PlatformAliases
            .AsNoTracking()
            .Where(a => a.Type == PlatformAliasType.ProviderMapping && a.Provider == provider)
            .Join(
                _context.Platforms,
                alias => alias.PlatformId,
                platform => platform.Id,
                (alias, platform) => new { platform.ShortName, ExternalId = alias.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.ShortName,
            r => r.ExternalId,
            StringComparer.OrdinalIgnoreCase);
    }
}
