namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Service for rematerializing metadata fields from layers.
///     Handles single-title, platform-wide, and global rematerialization.
/// </summary>
public interface IRematerializationService
{
    /// <summary>
    ///     Rematerializes a single title's metadata from its layers.
    /// </summary>
    Task RematerializeTitleAsync(int titleId, CancellationToken ct = default);

    /// <summary>
    ///     Rematerializes all titles for a platform.
    ///     Processes in batches to avoid memory pressure.
    /// </summary>
    Task RematerializePlatformAsync(int platformId, CancellationToken ct = default);

    /// <summary>
    ///     Rematerializes all titles across all platforms.
    ///     Used when global source priority changes.
    /// </summary>
    Task RematerializeAllAsync(CancellationToken ct = default);
}
