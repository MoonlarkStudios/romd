namespace Romd.Domain.Catalog;

/// <summary>
///     Lifecycle state of a title with respect to the canonical catalog projection.
/// </summary>
public enum TitleCatalogState
{
    /// <summary>The title is backed by at least one canonical <c>CatalogRelease</c>.</summary>
    Active = 0,

    /// <summary>
    ///     The title has no canonical releases but is retained because it carries user or
    ///     curated state (tracking, field overrides, confirmed external IDs, primary media,
    ///     collection membership).
    /// </summary>
    UserOnly = 1
}
