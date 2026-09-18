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
    ///     Sets a field default for a platform. Creates or updates the entry.
    /// </summary>
    Task SetAsync(int platformId, string fieldName, string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears a field default for a platform.
    /// </summary>
    Task ClearAsync(int platformId, string fieldName,
        CancellationToken cancellationToken = default);
}
