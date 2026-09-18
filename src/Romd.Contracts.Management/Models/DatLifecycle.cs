namespace Romd.Contracts.Management.Models;

/// <summary>
///     Lifecycle of a DAT version within its source. Serialized as named strings
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity"). Mirrors
///     <c>Romd.Domain.Source.Dat.DatFileLifecycle</c> member-for-member; contracts cannot
///     reference the domain, so keep the two in sync.
/// </summary>
public enum DatLifecycle
{
    /// <summary>Ingested as a replacement but not yet activated.</summary>
    PendingActivation = 0,

    /// <summary>The source's current version.</summary>
    Active = 1,

    /// <summary>A retained prior version, replaced by a newer activation.</summary>
    Superseded = 2
}
