namespace Romd.Admin.Application.Source.Platform;

/// <summary>
///     Port for Platform data access.
/// </summary>
public interface IPlatformRepository
{
    /// <summary>
    ///     Gets a platform by ID.
    /// </summary>
    Task<Domain.Source.Platform.Platform?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a platform by its short name (URL-friendly identifier).
    /// </summary>
    Task<Domain.Source.Platform.Platform?> GetByShortNameAsync(string shortName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all platforms ordered by name.
    /// </summary>
    Task<IReadOnlyList<Domain.Source.Platform.Platform>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds a new platform.
    /// </summary>
    Task<Domain.Source.Platform.Platform> AddAsync(Domain.Source.Platform.Platform platform, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds multiple platforms in a batch.
    /// </summary>
    Task AddRangeAsync(IReadOnlyList<Domain.Source.Platform.Platform> platforms, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes a platform by ID.
    /// </summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks if any platforms exist in the database.
    ///     Used by seeder to determine if seeding is needed.
    /// </summary>
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
}
