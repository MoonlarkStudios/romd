namespace Romd.Admin.Application.Source.Platform;

/// <summary>
///     Port for platform-level field default data access.
///     Stores per-field source preferences at the platform level (cascade step 2).
/// </summary>
public interface IPlatformFieldDefaultRepository
{
    Task LockAsync(int platformId, CancellationToken cancellationToken = default);
    Task<int> CountTitlesAsync(int platformId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all field defaults for a platform as a dictionary of fieldName → sourceId.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetByPlatformIdAsync(
        int platformId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages a new field default, replacing any earlier pending default for the same field.
    /// </summary>
    /// <remarks>
    ///     Requires the caller's transaction. Before replacing a persisted default, call
    ///     <see cref="ClearStagedAsync" /> and flush its deletions. This operation never flushes.
    /// </remarks>
    Task SetStagedAsync(int platformId, string fieldName, string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages deletion of every case-insensitive spelling of the field and cancels matching
    ///     pending defaults. Unrelated staged changes are preserved.
    /// </summary>
    /// <remarks>Requires the caller's transaction; flush before setting a persisted field again.</remarks>
    Task ClearStagedAsync(int platformId, string fieldName,
        CancellationToken cancellationToken = default);
}
