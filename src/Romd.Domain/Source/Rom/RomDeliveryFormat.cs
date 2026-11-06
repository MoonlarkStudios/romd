namespace Romd.Domain.Source.Rom;

/// <summary>Stable delivery identities, independent of storage encodings.</summary>
public enum RomDeliveryFormat
{
    Original = 0,
    Rvz = 1,
    Chd = 2
}

public enum RomDeliveryState
{
    Available = 0,
    Preparing = 1,
    Unsupported = 2
}
