using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     Generic repository for taxonomy entities (Region, GameLanguage, etc.).
///     Provides CRUD, alias management, and merge support.
/// </summary>
public interface ITaxonomyRepository<TEntity> where TEntity : class, ITaxonomyEntity
{
    Task<TEntity?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> GetAllAsync(CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task<TEntity> AddAsync(TEntity entity, CancellationToken ct = default);
    Task AddRangeAsync(IReadOnlyList<TEntity> entities, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    // Alias operations
    Task AddAliasAsync(int entityId, string normalizedAlias, CancellationToken ct = default);
    Task AddAliasesBatchAsync(int entityId, IReadOnlyList<string> normalizedAliases, CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Alias)>> GetAliasesAsync(int entityId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetAllAliasesAsync(CancellationToken ct = default);
    Task<bool> AliasExistsAsync(string normalizedAlias, CancellationToken ct = default);
    Task RemoveAliasAsync(int aliasId, CancellationToken ct = default);

    // Registered reference identities must not be removed by taxonomy merges.
    Task<bool> HasReferenceIdentityAsync(int id, CancellationToken ct = default);

    // Merge operations
    Task ReassignAliasesAsync(int sourceId, int targetId, CancellationToken ct = default);
    Task ReassignJunctionsAsync(int sourceId, int targetId, CancellationToken ct = default);

    // Eager-loaded query (avoids N+1)
    Task<IReadOnlyList<(TEntity Entity, bool CanMerge, IReadOnlyList<(int Id, string Alias, string Ownership)> Aliases)>>
        GetAllWithAliasesAsync(CancellationToken ct = default);
}
