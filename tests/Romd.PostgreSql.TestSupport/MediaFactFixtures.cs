using System.Security.Cryptography;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.PostgreSql.TestSupport;

/// <summary>Typed synthetic observations. These establish fixture consistency, never native/runtime qualification.</summary>
public static class MediaFactFixtures
{
    public static RomLogicalIdentity Identity(ReadOnlySpan<byte> bytes) => new(bytes.Length,
        Sha256.FromSpan(SHA256.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)), Md5.FromSpan(MD5.HashData(bytes)),
        Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));

    public static RomContainerFacts.Rvz Rvz(long logicalBytes, uint discType, uint compression, int level, int blockBytes,
        bool hasWiiPartitions) => new(0x00030000, discType, compression, level, blockBytes, hasWiiPartitions, logicalBytes);

    public static RomContainerFacts.Chd Chd(long logicalBytes, uint hunkBytes, uint unitBytes, IReadOnlyList<uint> codecs,
        Sha1 rawSha1, Sha1 overallSha1, IReadOnlyList<RomChdMetadataEntry> metadata) => new(5, logicalBytes, hunkBytes, unitBytes,
            checked((uint)((logicalBytes + hunkBytes - 1) / hunkBytes)), codecs, false, rawSha1, overallSha1, metadata);

    public static RomContainerFacts.Chd DvdChd(RomChdIntegrity observation)
    {
        var p = observation.Parameters;
        return Chd(p.LogicalBytes, p.HunkBytes, p.UnitBytes, [p.Codec0, p.Codec1, p.Codec2, p.Codec3],
            observation.StoredRawSha1, observation.StoredOverallSha1, [new(0x44564420, 1, new byte[] { 0 })]);
    }

    public static RomRepresentationVerification WholeMeasurement(Guid id, Sha256 artifactSha256, long artifactBytes,
        RomLogicalIdentity logical, RomContainerFacts container, RomDecoderIdentity decoder, string checkedProfile, string verifier,
        DateTimeOffset observedAt, Sha1? computedRawSha1, Sha1? computedOverallSha1) => new(id, artifactSha256, artifactBytes,
            decoder, checkedProfile, verifier, observedAt, [new(RomOutputSelector.Whole.Instance, logical)], container,
            computedRawSha1, computedOverallSha1);

    public static RomPs2BootLayoutFacts Ps2Boot(long logicalBytes, string inspector) => new(logicalBytes, 64, 16, 20, 2048, 22, 64, 24, 128,
        @"cdrom0:\SLUS_203.12;1", @"cdrom0:\SLUS_203.12;1", "SLUS_203.12", 0x1000, 1, RomPs2BootLayoutFacts.ProfileVersion, inspector);

    public static RomLogicalInspection ObserveNintendo(Guid id, RomLogicalIdentity identity, ReadOnlySpan<byte> prefix,
        RomNintendoLayoutFacts layout, string profile, string inspector, DateTimeOffset observedAt) =>
        new(id, identity, profile, inspector, observedAt, prefix, layout: layout);

    public static RomLogicalInspection ObservePs2(Guid id, RomLogicalIdentity identity, ReadOnlySpan<byte> prefix,
        RomPs2BootLayoutFacts boot, string profile, string inspector, DateTimeOffset observedAt) =>
        new(id, identity, profile, inspector, observedAt, prefix, layout: boot);
}
