using Romd.Domain.Source.Platform;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Source.Platform;

/// <summary>
///     Port for PlatformAlias data access.
/// </summary>
public interface IPlatformAliasRepository
{
    Task<PagedList<PlatformAlias>> ListPageAsync(int platformId, int? after, int limit, CancellationToken ct = default);

    /// <summary>
    ///     Gets all aliases for a platform, name aliases first.
    /// </summary>
    Task<IReadOnlyList<PlatformAlias>> GetByPlatformIdAsync(int platformId, CancellationToken ct = default);

    /// <summary>
    ///     Gets a single alias by ID.
    /// </summary>
    Task<PlatformAlias?> GetByIdAsync(int aliasId, CancellationToken ct = default);

    /// <summary>
    ///     Stages a new, identity-free alias in the caller's transaction. The caller flushes
    ///     creation and reads its own insertion's generated identity from the receipt.
    /// </summary>
    Task<IStagedPlatformAliasCreation> AddStagedAsync(PlatformAlias alias, CancellationToken ct = default);

    /// <summary>
    ///     Removes an alias by ID.
    /// </summary>
    Task RemoveAsync(int aliasId, CancellationToken ct = default);

    /// <summary>
    ///     Checks whether a name alias with the given normalized value exists on any platform.
    /// </summary>
    Task<bool> NameAliasExistsAsync(string normalizedValue, CancellationToken ct = default);

    /// <summary>
    ///     Checks whether the platform already has a mapping for the given provider.
    /// </summary>
    Task<bool> ProviderMappingExistsAsync(int platformId, string provider, CancellationToken ct = default);

    /// <summary>
    ///     Checks whether any provider mapping exists for the given provider,
    ///     across all platforms. Used by seeding to decide idempotently.
    /// </summary>
    Task<bool> AnyProviderMappingsAsync(string provider, CancellationToken ct = default);

    /// <summary>
    ///     Checks whether any name aliases exist at all. Used by seeding.
    /// </summary>
    Task<bool> AnyNameAliasesAsync(CancellationToken ct = default);

    /// <summary>
    ///     Gets all name aliases as (PlatformId, NormalizedValue) pairs,
    ///     for building the DAT header routing lookup.
    /// </summary>
    Task<IReadOnlyList<(int PlatformId, string NormalizedValue)>> GetAllNameAliasesAsync(CancellationToken ct = default);

    /// <summary>
    ///     Gets the provider's platform mappings keyed by platform short name
    ///     (case-insensitive), valued by the provider's external platform ID.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetProviderMappingsByShortNameAsync(
        string provider,
        CancellationToken ct = default);
}

/// <summary>
///     Reads the created alias after the caller flushes. This scope-bound receipt does not flush
///     or commit creation, and retains the insertion's own generated identity.
/// </summary>
public interface IStagedPlatformAliasCreation
{
    PlatformAlias ReadAfterFlush();
}
