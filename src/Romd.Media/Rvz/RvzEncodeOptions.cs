namespace Romd.Media.Rvz;

public enum RvzEncodeCompression : uint { None = 0, Zstandard = 5 }

/// <summary>Explicit container parameters and resource ceilings; these are not a canonical recipe.</summary>
public sealed record RvzEncodeOptions(RvzEncodeCompression Compression, int CompressionLevel, int ChunkBytes)
{
    public long MaxLogicalBytes { get; init; } = 16L << 30;
    public long MaxOutputBytes { get; init; } = 16L << 30;
    public bool PackJunk { get; init; } = true;

    internal string? InvalidReason() =>
        Compression is not (RvzEncodeCompression.None or RvzEncodeCompression.Zstandard) ? "Unsupported RVZ output compression." :
        Compression == RvzEncodeCompression.None && CompressionLevel != 0 ? "Uncompressed RVZ requires compression level zero." :
        Compression == RvzEncodeCompression.Zstandard && CompressionLevel is < 1 or > 22 ? "Zstandard compression level must be between 1 and 22." :
        ChunkBytes is < 0x8000 or > 0x800000 ||
            (ChunkBytes < 0x200000 ? (ChunkBytes & (ChunkBytes - 1)) != 0 : ChunkBytes % 0x200000 != 0) ? "Unsupported RVZ output chunk size." :
        MaxLogicalBytes is <= 0x80 or > 16L << 30 ? "RVZ logical byte limit is outside reader bounds." :
        MaxOutputBytes is < 0x124 or > 64L << 30 ? "RVZ output byte limit is outside bounds." : null;
}
