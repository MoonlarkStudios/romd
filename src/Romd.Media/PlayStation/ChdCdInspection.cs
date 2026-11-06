using Moonlark.Libchdr;

namespace Romd.Media.PlayStation;

/// <summary>A measured track-local BIN projection, including stored pregap and excluding CHD alignment.
/// This does not establish the source file boundaries, original descriptor, DAT match or console family.</summary>
public sealed record ChdCdTrackInspection(uint Number, ChdCdTrackType Type, ulong FirstFrame, uint Frames,
    uint AlignmentFrames, uint PregapFrames, bool PregapStored, uint PostgapFrames,
    long BinBytes, string Sha256, string Sha1, string Md5, string Crc32);

/// <summary>Exact ordered metadata payload bytes, carried as bounded lowercase hex across the worker boundary.</summary>
public sealed record ChdCdMetadataInspection(uint Tag, byte Flags, string PayloadHex);

/// <summary>Complete CD container integrity, exact metadata and independent track projections. The raw CHD digest covers
/// padded 2,448-byte frames; it is deliberately separate from every projected BIN digest.</summary>
public sealed record ChdCdInspection(string Profile, string ProjectionProfile, string ArtifactSha256,
    uint Version, bool HasParent, ChdHeaderFacts Header, string StoredRawSha1, string ComputedRawSha1,
    string StoredOverallSha1, string ComputedOverallSha1, IReadOnlyList<ChdCdTrackInspection> Tracks, IReadOnlyList<ChdCdMetadataInspection> Metadata,
    Ps1CdObservedFacts? Ps1Boot);

/// <summary>Inspection bounds, not a storage recipe or platform qualification.</summary>
public static class ChdCdLimits
{
    public const long MaximumArtifactBytes = 2L << 30;
    public const uint MaximumFrames = 450000;
    public const long MaximumLogicalBytes = (long)MaximumFrames * ChdCdImage.FrameBytes;
    public const uint MaximumHunkBytes = ChdCdImage.FrameBytes * 256;
    public const int MaximumTracks = 99;
    public static ChdHeaderPreflightLimits Preflight { get; } =
        new(MaximumArtifactBytes, (ulong)MaximumLogicalBytes, MaximumHunkBytes, MaximumFrames);
}
