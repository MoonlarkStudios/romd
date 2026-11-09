using Moonlark.Libchdr;

namespace Romd.Media.Chd;

/// <summary>The single set of DVD CHD container bounds and codec classes shared by preflight, inspection,
/// encoding, verification and the worker protocol.</summary>
public static class ChdDvdLimits
{
    /// <summary>The DVD logical unit every DVD-shaped CHD uses, independent of any recipe version.</summary>
    public const uint UnitBytes = 2048;
    public const long MaximumArtifactBytes = 10L << 30;
    public const long MaximumLogicalBytes = 10L << 30;
    public const uint MaximumHunkBytes = 1U << 20;
    public const uint MaximumHunkCount = 2621440;

    public static ChdHeaderPreflightLimits Preflight { get; } =
        new(MaximumArtifactBytes, MaximumLogicalBytes, MaximumHunkBytes, MaximumHunkCount);

    /// <summary>Codecs a DVD-shaped CHD may name in any slot; None marks an unused slot.</summary>
    public static bool IsDvdCodec(uint codec) => (ChdCodec)codec is ChdCodec.None or ChdCodec.Zlib or ChdCodec.Lzma or
        ChdCodec.Huffman or ChdCodec.Flac or ChdCodec.Zstd;

    /// <summary>CD-sector codecs: header preflight admits them so DVD inspection can reject them precisely.</summary>
    public static bool IsCdCodec(uint codec) => (ChdCodec)codec is ChdCodec.CdZlib or ChdCodec.CdLzma or
        ChdCodec.CdFlac or ChdCodec.CdZstd;
}
