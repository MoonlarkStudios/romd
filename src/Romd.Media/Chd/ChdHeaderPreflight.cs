namespace Romd.Media.Chd;

public sealed record ChdHeaderPreflightLimits(long MaximumArtifactBytes, ulong MaximumLogicalBytes,
    uint MaximumHunkBytes, uint MaximumHunkCount);

public sealed record ChdHeaderFacts(long ArtifactBytes, ulong LogicalBytes, uint HunkBytes, uint UnitBytes,
    uint HunkCount, uint Codec0, uint Codec1, uint Codec2, uint Codec3);

public enum ChdHeaderPreflightCause
{
    TruncatedHeader,
    InvalidSignature,
    InvalidHeaderLength,
    UnsupportedVersion,
    ParentReference,
    InvalidArtifactLength,
    ArtifactLimitExceeded,
    UnsupportedCodec,
    ZeroLogicalBytes,
    ZeroHunkBytes,
    ZeroUnitBytes,
    LogicalLimitExceeded,
    HunkLimitExceeded,
    HunkCountOverflow,
    HunkCountLimitExceeded
}

public abstract record ChdHeaderPreflightResult
{
    private protected ChdHeaderPreflightResult() { }

    public sealed record Accepted(ChdHeaderFacts Facts) : ChdHeaderPreflightResult;

    public sealed record Rejected(ChdHeaderPreflightCause Cause, int? CodecSlot = null,
        uint? Codec = null) : ChdHeaderPreflightResult;
}

/// <summary>Fixed-header admission only; no decoded-integrity or memory-qualification claim.</summary>
public static class ChdHeaderPreflight
{
    public static ChdHeaderPreflightResult Inspect(ReadOnlySpan<byte> header, long observedArtifactBytes,
        ChdHeaderPreflightLimits limits)
    {
        if (header.Length < 124)
            return Reject(ChdHeaderPreflightCause.TruncatedHeader);
        if (!header[..8].SequenceEqual("MComprHD"u8))
            return Reject(ChdHeaderPreflightCause.InvalidSignature);
        if (ReadUInt32(header, 8) != 124)
            return Reject(ChdHeaderPreflightCause.InvalidHeaderLength);
        if (ReadUInt32(header, 12) != 5)
            return Reject(ChdHeaderPreflightCause.UnsupportedVersion);

        foreach (byte value in header.Slice(104, 20))
        {
            if (value != 0)
                return Reject(ChdHeaderPreflightCause.ParentReference);
        }

        if (observedArtifactBytes < 124)
            return Reject(ChdHeaderPreflightCause.InvalidArtifactLength);
        if (observedArtifactBytes > limits.MaximumArtifactBytes)
            return Reject(ChdHeaderPreflightCause.ArtifactLimitExceeded);

        Span<uint> codecs = stackalloc uint[4];
        for (int slot = 0; slot < codecs.Length; slot++)
        {
            uint codec = ReadUInt32(header, 16 + (slot * 4));
            if (!ChdDvdLimits.IsDvdCodec(codec) && !ChdDvdLimits.IsCdCodec(codec))
                return Reject(ChdHeaderPreflightCause.UnsupportedCodec, slot, codec);
            codecs[slot] = codec;
        }

        ulong logicalBytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header.Slice(32, 8));
        uint hunkBytes = ReadUInt32(header, 56);
        uint unitBytes = ReadUInt32(header, 60);
        if (logicalBytes == 0)
            return Reject(ChdHeaderPreflightCause.ZeroLogicalBytes);
        if (hunkBytes == 0)
            return Reject(ChdHeaderPreflightCause.ZeroHunkBytes);
        if (unitBytes == 0)
            return Reject(ChdHeaderPreflightCause.ZeroUnitBytes);
        if (logicalBytes > limits.MaximumLogicalBytes)
            return Reject(ChdHeaderPreflightCause.LogicalLimitExceeded);
        if (hunkBytes > limits.MaximumHunkBytes)
            return Reject(ChdHeaderPreflightCause.HunkLimitExceeded);

        // Quotient plus remainder avoids adding hunkBytes to an untrusted logical size.
        ulong hunkCount = checked((logicalBytes / hunkBytes) + (logicalBytes % hunkBytes == 0 ? 0UL : 1UL));
        if (hunkCount > uint.MaxValue)
            return Reject(ChdHeaderPreflightCause.HunkCountOverflow);
        if (hunkCount > limits.MaximumHunkCount)
            return Reject(ChdHeaderPreflightCause.HunkCountLimitExceeded);

        return new ChdHeaderPreflightResult.Accepted(new ChdHeaderFacts(observedArtifactBytes, logicalBytes,
            hunkBytes, unitBytes, checked((uint)hunkCount), codecs[0], codecs[1], codecs[2], codecs[3]));

        static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));

        static ChdHeaderPreflightResult Reject(ChdHeaderPreflightCause cause, int? codecSlot = null,
            uint? codec = null) => new ChdHeaderPreflightResult.Rejected(cause, codecSlot, codec);
    }
}
