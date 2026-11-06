namespace Romd.Domain.Catalog;

/// <summary>
///     Status of the canonical catalog projection for a platform. Set <see cref="Dirty"/>
///     before a rebuild and <see cref="Clean"/> atomically with a successful rebuild; a
///     failed rebuild leaves the platform <see cref="Failed"/>. Non-Clean platforms are
///     rebuilt by the worker recovery dispatcher, and library materialization is gated
///     while any dependent platform is non-Clean.
/// </summary>
public enum CatalogRebuildState
{
    Clean = 0,
    Dirty = 1,
    Failed = 2
}
