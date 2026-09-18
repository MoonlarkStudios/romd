namespace Romd.Domain.Source.Dat;

/// <summary>
///     Lifecycle of a DAT file version within its <see cref="DatSource" />.
///     Exactly one version per source may be Active and at most one PendingActivation;
///     both invariants are enforced by partial unique indexes in persistence.
/// </summary>
public enum DatFileLifecycle
{
    /// <summary>Ingested as a replacement but not yet activated.</summary>
    PendingActivation = 0,

    /// <summary>The source's current version.</summary>
    Active = 1,

    /// <summary>A retained prior version, replaced by a newer activation.</summary>
    Superseded = 2
}
