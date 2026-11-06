using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Conservative output reservation for the fixed parent-free DVD recipe geometry.</summary>
/// <remarks>Derived from pinned MAME 0.289 writer source; this does not qualify encoding or activate the recipe.
/// The bound depends only on hunk and unit geometry: every codec falls back to a raw hunk when compression is not
/// smaller, and the map bit widths below are codec-independent at the 16384-byte hunk.</remarks>
public static class ChdDvdOutputBounds
{
    private const long _maximumConfiguredLogicalBytes = 16L << 30;
    private const long _maximumDvdLogicalBytes = RomChdDvdLimits.MaximumLogicalBytes;
    private const long _maximumEncodedBytes = RomChdDvdLimits.MaximumArtifactBytes;
    private const int _headerBytes = 124, _dvdMetadataBytes = 16 + 1, _mapHeaderBytes = 16;
    // Sixteen Huffman code lengths, each at most two four-bit values (the escape for length 1).
    private const int _maximumMapTreeBits = 16 * 2 * 4;
    // Pinned writer bounds: type/RLE 12, bits_for_value(16384) = 15, CRC 16.
    // Parent-free self references fit 20 bits at the existing 10 GiB geometry, so 31 payload bits dominate.
    private const int _maximumMapBitsPerHunk = 12 + 15 + 16;

    public static long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maxLogicalBytes is <= 0x80 or > _maximumConfiguredLogicalBytes ||
            logicalBytes < ChdEncodingRecipe.UnitBytes || logicalBytes > maxLogicalBytes ||
            logicalBytes > _maximumDvdLogicalBytes || logicalBytes % ChdEncodingRecipe.UnitBytes != 0)
            return 0;
        long hunks = checked((logicalBytes - 1) / ChdEncodingRecipe.HunkBytes + 1);
        // The compressor falls back to a complete raw hunk when compression is not smaller,
        // including the final partial hunk. Compressed-container append calls use alignment 0.
        long dataBytes = checked(hunks * ChdEncodingRecipe.HunkBytes);
        long mapBytes = checked(_mapHeaderBytes + (_maximumMapTreeBits + _maximumMapBitsPerHunk * hunks + 7) / 8);
        long maximum = checked(_headerBytes + _dvdMetadataBytes + dataBytes + mapBytes);
        ct.ThrowIfCancellationRequested();
        return maximum <= _maximumEncodedBytes ? maximum : 0;
    }
}
