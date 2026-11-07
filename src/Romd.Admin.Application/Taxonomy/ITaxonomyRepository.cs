using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Management.Taxonomy;
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

    /// <summary>
    ///     Takes the minting lock shared by regions and languages until the caller's transaction ends, and throws
    ///     <see cref="InvalidOperationException" /> when there is none. The resolver takes it before it writes any
    ///     region, language or alias, so an ingest waiting for it holds nothing the lock's holder needs to mint.
    /// </summary>
    Task LockMintingAsync(CancellationToken ct = default);

    /// <summary>
    ///     Stages a new identity-free unregistered row and mints its key from <paramref name="normalizedToken" />.
    ///     Requires the caller's transaction and takes the shared mint lock. The caller flushes before reading its ID.
    /// </summary>
    Task<IStagedTaxonomyRow> AddStagedAsync(TEntity entity, string normalizedToken, CancellationToken ct = default);

    /// <summary>
    ///     Protects one database-only mint attempt in the caller's transaction. Flushes earlier caller staging before
    ///     the savepoint; the callback owns new row/alias staging and identity flushes. Null rolls back only the
    ///     attempt and detaches only tracking it acquired, including audit evidence. Never ends the outer transaction.
    ///     The caller holds <see cref="LockMintingAsync" /> before entering this protocol.
    /// </summary>
    Task<int?> ExecuteMintSavepointAsync(Func<CancellationToken, Task<int?>> operation, CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>The internal id of the row with this natural key, registered or not.</summary>
    Task<int?> GetIdByKeyAsync(string key, CancellationToken ct = default);

    // Alias operations
    /// <summary>Stages an installation-owned alias in the caller's transaction; its owner flushes before reading it.</summary>
    Task<IStagedTaxonomyAlias> AddAliasStagedAsync(int entityId, string normalizedAlias, CancellationToken ct = default);

    /// <summary>Classifies a unique failure from the alias flush; never use this around row creation or other writes.</summary>
    bool IsAliasConflict(Exception error);

    /// <summary>
    ///     The id of the row the alias names, null when none does. Locks the row <c>FOR KEY SHARE</c> until the
    ///     caller's transaction ends, as the links an ingest inserts to it do, so a merge cannot remove it meanwhile.
    /// </summary>
    Task<int?> LockByAliasAsync(string normalizedAlias, CancellationToken ct = default);

    Task<bool> AliasExistsAsync(string normalizedAlias, CancellationToken ct = default);
    /// <summary>
    ///     Stages removing the alias when it belongs to <paramref name="entityId" />; false when the row has no such
    ///     alias. The caller locks the row first, so nothing a merge may move is loaded before the lock.
    /// </summary>
    Task<bool> RemoveAliasAsync(int entityId, int aliasId, CancellationToken ct = default);

    // Registered reference identities must not be removed by taxonomy merges.
    Task<bool> HasReferenceIdentityAsync(int id, CancellationToken ct = default);

    // Merge operations

    /// <summary>
    ///     Locks each existing row among <paramref name="rows" /> in its named mode, in id order, and returns the ids it
    ///     locked. Merges and alias writes take it, so they serialize on a row and see whether a concurrent merge
    ///     removed it.
    /// </summary>
    Task<IReadOnlySet<int>> LockAsync(IReadOnlyCollection<(int Id, TaxonomyRowLock Mode)> rows, CancellationToken ct = default);

    /// <summary>
    ///     Bounds every lock wait for the rest of the caller's transaction, which it requires: a wait past
    ///     <paramref name="timeout" /> fails as <see cref="Common.Persistence.TransactionConflict.LockTimeout" />.
    /// </summary>
    Task LimitLockWaitsAsync(TimeSpan timeout, CancellationToken ct = default);

    Task ReassignAliasesAsync(int sourceId, int targetId, CancellationToken ct = default);
    Task ReassignJunctionsAsync(int sourceId, int targetId, CancellationToken ct = default);

    /// <summary>What merging the source row moves: its aliases, DAT game links and catalog release links.</summary>
    Task<TaxonomyMergeImpact> GetMergeImpactAsync(int sourceId, CancellationToken ct = default);

    // The admin management projection: every row, registered or not, matching the request's query, each with its first
    // alias page (avoids N+1).
    Task<PagedList<TaxonomyEntry>> ListWithAliasesAsync(TaxonomyPageRequest request, CancellationToken ct = default);
    Task<TaxonomyEntry?> GetWithAliasesAsync(string key, CancellationToken ct = default);

    /// <summary>One page of the row's aliases in ascending id order, after the request's cursor.</summary>
    Task<PagedList<TaxonomyAlias>> ListAliasesAsync(int entityId, TaxonomyAliasPageRequest request, CancellationToken ct = default);
}
