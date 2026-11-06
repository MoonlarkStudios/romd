using Romd.Domain.Source.Rom;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;

namespace Romd.Infrastructure.Storage;

/// <summary>Translates domain disc kinds to the Media-owned RVZ family without duplicating codec rules.</summary>
public sealed class RvzDiscFamily
{
    private RvzDiscFamily(RomIsoDiscKind discKind, RvzCodecFamily codec)
    {
        DiscKind = discKind;
        Codec = codec;
    }

    public static RvzDiscFamily GameCube { get; } = new(RomIsoDiscKind.GameCube, RvzCodecFamily.GameCube);
    public static RvzDiscFamily Wii { get; } = new(RomIsoDiscKind.Wii, RvzCodecFamily.Wii);
    private static readonly RvzDiscFamily[] Families = [GameCube, Wii];

    public static RvzDiscFamily? For(RomIsoDiscKind? kind) => Array.Find(Families, family => family.DiscKind == kind);
    public static RvzDiscFamily? For(NintendoDiscKind? kind) => Array.Find(Families, family => family.Codec.DiscKind == kind);
    public RomIsoDiscKind DiscKind { get; }
    public RvzCodecFamily Codec { get; }
    public uint ContainerDiscType => Codec.ContainerDiscType;
    public bool MatchesHeader(ReadOnlySpan<byte> prefix) => Codec.MatchesHeader(prefix);
    public bool MatchesLayout(RomNintendoLayoutFacts layout) => Codec.MatchesLayout(layout.Partitions);
    public RvzCanonicalOutputBound GetOutputBound(long logicalBytes, long maxLogicalBytes, CancellationToken ct) =>
        Codec.GetOutputBound(logicalBytes, maxLogicalBytes, ct);
}
