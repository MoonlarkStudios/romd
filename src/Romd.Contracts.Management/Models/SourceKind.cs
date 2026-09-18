namespace Romd.Contracts.Management.Models;

/// <summary>
///     Provider kind of a catalog source. Serialized as named strings
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity"). Mirrors
///     <c>Romd.Domain.Catalog.CatalogSourceKind</c> member-for-member; contracts cannot
///     reference the domain, so keep the two in sync.
/// </summary>
public enum SourceKind
{
    /// <summary>A DAT source; entries come from its Active version's parsed games.</summary>
    Dat = 1,

    /// <summary>A bulk import source.</summary>
    Import = 2,

    /// <summary>A manually curated source.</summary>
    Manual = 3
}
