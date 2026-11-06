namespace Romd.Domain.Source.Rom;

/// <summary>Measured ISO9660 UMD game layout with matching UMD/SFO identifiers and a PSP executable header.</summary>
public sealed record RomPspLayoutFacts : RomLayoutObservation
{
    public const string Profile = "psp-iso-umd-game/v1";
    public const long MaximumLogicalBytes = 2L << 30;

    public RomPspLayoutFacts(long logicalBytes, uint volumeBlocks, string discId, string discVersion)
    {
        ArgumentNullException.ThrowIfNull(discId);
        ArgumentNullException.ThrowIfNull(discVersion);
        if (logicalBytes is < 18 * 2048 or > MaximumLogicalBytes || logicalBytes % 2048 != 0 ||
            volumeBlocks < 18 || (ulong)volumeBlocks * 2048 > (ulong)logicalBytes ||
            discId.Length != 9 || discId.AsSpan(0, 4).IndexOfAnyExceptInRange('A', 'Z') >= 0 ||
            discId.AsSpan(4).IndexOfAnyExceptInRange('0', '9') >= 0 || discVersion.Length != 4 ||
            !char.IsAsciiDigit(discVersion[0]) || discVersion[1] != '.' ||
            !char.IsAsciiDigit(discVersion[2]) || !char.IsAsciiDigit(discVersion[3]))
            throw new ArgumentException("PSP observations require bounded volume geometry and exact UMD/SFO identifiers.");
        LogicalBytes = logicalBytes;
        VolumeBlocks = volumeBlocks;
        DiscId = discId;
        DiscVersion = discVersion;
    }
    public long LogicalBytes { get; }
    public uint VolumeBlocks { get; }
    public string DiscId { get; }
    public string DiscVersion { get; }
    public override bool Describes(RomLogicalIdentity identity) => LogicalBytes == identity.Size;
}
