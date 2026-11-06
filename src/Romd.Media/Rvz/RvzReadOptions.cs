namespace Romd.Media.Rvz;

/// <summary>Limits applied before any size-dependent allocation. Validate before use.</summary>
public sealed record RvzReadOptions
{
    public long MaxLogicalBytes { get; init; } = 16L << 30;
    public int MaxChunkBytes { get; init; } = 8 << 20;
    /// <summary>Decode the chunks of one Wii region concurrently: lower latency, more cores per read.</summary>
    public bool ParallelRegionDecode { get; init; }

    public RvzReadOptions Validated()
    {
        if (MaxLogicalBytes is < 0x80 or > 64L << 30 || MaxChunkBytes is < 0x8000 or > 32 << 20)
            throw new InvalidOperationException("Invalid RVZ read limits.");
        return this;
    }
}

/// <summary>Container facts from the validated RVZ header.</summary>
public sealed record RvzDescriptor(uint Version, uint DiscType, uint Compression, int CompressionLevel, int ChunkSize, bool HasWiiPartitions);
