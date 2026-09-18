using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Taxonomy;

namespace Romd.Infrastructure.Taxonomy;

/// <summary>
///     Generic taxonomy resolver that resolves comma-separated tokens into entity IDs.
///     Auto-creates unknown tokens as new entities. Parameterized via entity factory delegate.
/// </summary>
public sealed class TaxonomyResolver<TEntity> : ITaxonomyResolver<TEntity>
    where TEntity : class, ITaxonomyEntity
{
    private readonly ITaxonomyRepository<TEntity> _repository;
    private readonly TaxonomyAliasCache _cache;
    private readonly string _cacheKey;
    private readonly Func<string, TEntity> _entityFactory;

    public TaxonomyResolver(
        ITaxonomyRepository<TEntity> repository,
        TaxonomyAliasCache cache,
        string cacheKey,
        Func<string, TEntity> entityFactory)
    {
        _repository = repository;
        _cache = cache;
        _cacheKey = cacheKey;
        _entityFactory = entityFactory;
    }

    public async Task<IReadOnlyList<int>> ResolveAsync(string? rawInput, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return [];

        await _cache.EnsureLoadedAsync(_cacheKey, () => _repository.GetAllAliasesAsync(ct), ct);

        var tokens = rawInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new HashSet<int>();

        foreach (var token in tokens)
        {
            var normalized = token.ToLowerInvariant();

            if (_cache.TryGetId(_cacheKey, normalized, out var id))
            {
                ids.Add(id);
                continue;
            }

            id = await AutoCreateAsync(token, normalized, ct);
            ids.Add(id);
        }

        return ids.ToList();
    }

    public void InvalidateCache() => _cache.Invalidate(_cacheKey);

    private async Task<int> AutoCreateAsync(string originalToken, string normalized, CancellationToken ct)
    {
        await _cache.AcquireCreationLockAsync(_cacheKey, ct);
        try
        {
            // Double-check after acquiring lock
            if (_cache.TryGetId(_cacheKey, normalized, out var existingId))
                return existingId;

            var entity = _entityFactory(originalToken);
            entity = await _repository.AddAsync(entity, ct);
            await _repository.AddAliasAsync(entity.Id, normalized, ct);
            _cache.AddAlias(_cacheKey, normalized, entity.Id);

            return entity.Id;
        }
        finally
        {
            _cache.ReleaseCreationLock(_cacheKey);
        }
    }
}
