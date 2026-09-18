namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to manually associate a title with an external provider ID.
///     This allows users to correct automatic matches or manually link titles
///     to their metadata in external providers (IGDB, ScreenScraper, etc.).
/// </summary>
public sealed record AssociateExternalIdRequest
{
    /// <summary>
    ///     The provider identifier (e.g., "igdb", "screenscraper").
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    ///     The external ID in the provider's system.
    /// </summary>
    public required string ExternalId { get; init; }
}
