using ZstdSharp;

namespace Romd.Media.Rvz;

/// <summary>Per-input output reservations for the owner-approved canonical GameCube and Wii recipe.</summary>
public static class RvzCanonicalBounds
{
    private const long MaximumLogicalBytes = 16L << 30;
    private const int HeaderBytes = 0x124, RawTableBytes = 24, GroupEntryBytes = 12;

    public static RvzCanonicalOutputBound GetGameCubeOutputBound(long logicalBytes, long maxLogicalBytes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maxLogicalBytes is <= 0x80 or > MaximumLogicalBytes)
            return new RvzCanonicalOutputBound.Failure(RvzEncodeFailureKind.InvalidOptions,
                "RVZ logical byte limit is outside reader bounds.");
        if (logicalBytes <= 0x80)
            return new RvzCanonicalOutputBound.Failure(RvzEncodeFailureKind.InvalidSource,
                "Truncated GameCube disc header.");
        if (logicalBytes > maxLogicalBytes)
            return new RvzCanonicalOutputBound.Failure(RvzEncodeFailureKind.LogicalLimitExceeded,
                "Disc exceeds the logical encoding limit.");

        int groups = checked((int)((logicalBytes - 1) / RvzCanonicalRecipe.ChunkSize + 1));
        int tableBytes = checked(groups * GroupEntryBytes);
        // Packing and group compression are retained only when smaller than their source.
        // Each stored group may need three alignment bytes. Both descriptor tables are
        // always compressed, so their codec expansion bounds belong in the reservation.
        long maximumOutputBytes = checked(HeaderBytes + logicalBytes + 3L * groups +
            Compressor.GetCompressBound(RawTableBytes) + Compressor.GetCompressBound(tableBytes));
        ct.ThrowIfCancellationRequested();
        return new RvzCanonicalOutputBound.Success(maximumOutputBytes);
    }

    public static RvzCanonicalOutputBound GetWiiOutputBound(long logicalBytes, long maxLogicalBytes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maxLogicalBytes is <= 0x80 or > MaximumLogicalBytes)
            return new RvzCanonicalOutputBound.Failure(RvzEncodeFailureKind.InvalidOptions,
                "RVZ logical byte limit is outside reader bounds.");
        if (logicalBytes <= 0x80)
            return new RvzCanonicalOutputBound.Failure(RvzEncodeFailureKind.InvalidSource, "Truncated Wii disc header.");
        if (logicalBytes > maxLogicalBytes)
            return new RvzCanonicalOutputBound.Failure(RvzEncodeFailureKind.LogicalLimitExceeded,
                "Disc exceeds the logical encoding limit.");
        long sectors = logicalBytes / 32768;
        long partitionGroups = (sectors + 3) / 4 + 63;
        long groups = (logicalBytes - 1) / RvzCanonicalRecipe.ChunkSize + 1 + 128;
        // A four-sector chunk has at most 52*4 exceptions of 22 bytes plus a
        // two-byte count: 4578 bytes. N includes full 32768-byte source sectors;
        // encoded sector data is 31744 plus 52*22 exception bytes, so the extra
        // cost is 120/sector. Each partition group adds its two-byte count and
        // at most three fallback-prefix padding bytes. Up to 64 partitions split
        // encrypted chunks; up to 65 raw areas split raw chunks. Alignment, the
        // 64*48 partition table and both compressed tables remain separate costs.
        long maximumOutputBytes = checked(HeaderBytes + logicalBytes + 120 * sectors + 5 * partitionGroups +
            3 * groups + 3072 + Compressor.GetCompressBound(1560) +
            Compressor.GetCompressBound(checked((int)(GroupEntryBytes * groups))));
        ct.ThrowIfCancellationRequested();
        return new RvzCanonicalOutputBound.Success(maximumOutputBytes);
    }

    public static RvzEncodeOptions WiiOptions(long maxLogicalBytes, long maxOutputBytes) =>
        GameCubeOptions(maxLogicalBytes, maxOutputBytes);

    public static RvzEncodeOptions GameCubeOptions(long maxLogicalBytes, long maxOutputBytes) =>
        new((RvzEncodeCompression)RvzCanonicalRecipe.Compression, RvzCanonicalRecipe.CompressionLevel,
            RvzCanonicalRecipe.ChunkSize)
        {
            MaxLogicalBytes = maxLogicalBytes,
            MaxOutputBytes = maxOutputBytes,
            PackJunk = true
        };
}

public abstract record RvzCanonicalOutputBound
{
    private protected RvzCanonicalOutputBound() { }

    public sealed record Success(long MaximumOutputBytes) : RvzCanonicalOutputBound;

    public sealed record Failure(RvzEncodeFailureKind Kind, string Description) : RvzCanonicalOutputBound;
}
