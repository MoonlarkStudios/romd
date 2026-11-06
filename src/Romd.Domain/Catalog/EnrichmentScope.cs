namespace Romd.Domain.Catalog;

/// <summary>
///     Selects which titles a bulk enrichment run targets.
/// </summary>
public enum EnrichmentScope
{
    /// <summary>
    ///     Only titles in the curated collection (those with a <see cref="TrackedTitle" />). This is the
    ///     default: enrichment effort tracks the titles the curator owns or is hunting for.
    /// </summary>
    Tracked = 0,

    /// <summary>
    ///     Every title on the platform, regardless of tracking. An opt-in escape hatch for
    ///     curators who want to enrich the entire catalog.
    /// </summary>
    All = 1
}
