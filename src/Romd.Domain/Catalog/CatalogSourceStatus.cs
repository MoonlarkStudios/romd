namespace Romd.Domain.Catalog;

/// <summary>
///     Lifecycle status of a catalog source. Non-Active statuses gate effective reads and
///     subscription polling, never admin mutations. Stored as the enum member name.
///     See docs/decisions/neutral-source-identity.md.
/// </summary>
public enum CatalogSourceStatus
{
    Active = 1,
    Discontinued = 2,
    Disabled = 3
}
