using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Source.Platform;
using Romd.Application.Common.Pagination;
using System.Globalization;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class PlatformAliasRepository : IPlatformAliasRepository
{
    private readonly RomdDbContext _context;

    public PlatformAliasRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<PagedList<PlatformAlias>> ListPageAsync(int platformId, int? after, int limit, CancellationToken ct = default)
    {
        var rows = await _context.PlatformAliases.AsNoTracking().Where(x => x.PlatformId == platformId && (after == null || x.Id > after))
            .OrderBy(x => x.Id).Take(limit + 1).ToListAsync(ct);
        return new(rows.Take(limit).Select(x => x.ToDomain()).ToArray(), rows.Count > limit
            ? rows[limit - 1].Id.ToString(CultureInfo.InvariantCulture) : null, rows.Count > limit);
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

    public Task<IStagedPlatformAliasCreation> AddStagedAsync(PlatformAlias alias, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (alias.Id != 0)
            throw new ArgumentException("Alias creation requires a new alias without an identity.", nameof(alias));
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Alias creation requires the caller's transaction.");
        var entity = PlatformAliasEntity.FromDomain(alias);
        _context.PlatformAliases.Add(entity);
        return Task.FromResult<IStagedPlatformAliasCreation>(new StagedPlatformAliasCreation(entity));
    }

    private sealed class StagedPlatformAliasCreation(PlatformAliasEntity entity) : IStagedPlatformAliasCreation
    {
        public PlatformAlias ReadAfterFlush() => entity.Id > 0
            ? entity.ToDomain()
            : throw new InvalidOperationException("Flush staged alias creation before reading its identity.");
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
