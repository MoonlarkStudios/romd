namespace Romd.Contracts.Management.Models;

/// <summary>
///     Lifecycle status of a DAT's catalog source. Serialized as named strings
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity"). Mirrors
///     <c>Romd.Domain.Catalog.CatalogSourceStatus</c> member-for-member; contracts cannot
///     reference the domain, so keep the two in sync.
/// </summary>
public enum SourceLifecycleStatus
{
    /// <summary>The source's references count toward effective backing.</summary>
    Active = 1,

    /// <summary>Upstream authority gone; references hidden, fully reversible.</summary>
    Discontinued = 2,

    /// <summary>Operator choice; references hidden, fully reversible.</summary>
    Disabled = 3
}
