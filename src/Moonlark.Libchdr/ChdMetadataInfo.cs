namespace Moonlark.Libchdr;

/// <summary>A metadata entry's immutable tag, required buffer length and native flags.</summary>
/// <param name="Tag">The native FourCC, including unknown tags.</param>
/// <param name="Length">The full payload length, including any stored terminating NUL.</param>
/// <param name="Flags">The native flags; bit zero includes this entry in the overall SHA-1.</param>
public readonly record struct ChdMetadataInfo(ChdMetadataTag Tag, uint Length, byte Flags)
{
    /// <summary>Whether the entry participates in the overall hash.</summary>
    public bool IsChecksummed => (Flags & 1) != 0;
}
