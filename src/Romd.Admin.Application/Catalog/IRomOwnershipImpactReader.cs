namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Reads the catalog platforms affected by removing a stored ROM file.
/// </summary>
public interface IRomOwnershipImpactReader
{
    Task<IReadOnlyList<int>> ReadTitlePlatformIdsAsync(
        int romFileId,
        CancellationToken cancellationToken = default);
}
