using Romd.Domain.Source.Rom;
using Romd.Media.Inspection;

namespace Romd.Infrastructure.Storage;

internal static class RomLogicalInspectionMapper
{
    internal static RomLogicalInspection Map(RomVerification proof, MediaInspection measured, string inspectorBuild, RomPs2BootLayoutFacts? boot = null) =>
        new(Guid.NewGuid(), new(proof.ContentSize, proof.ContentSha256, proof.Sha1, proof.Md5, proof.Crc32), proof.Profile, inspectorBuild, proof.VerifiedAt,
            Convert.FromHexString(measured.LogicalPrefix), RomLayoutObservation.Exclusive(
                measured.NintendoLayout is { } nintendo ? new RomNintendoLayoutFacts(nintendo.Partitions, nintendo.StandardKeyPartitions, nintendo.KoreanKeyPartitions) : null,
                boot,
                measured.Psp is { } psp ? new RomPspLayoutFacts(psp.LogicalBytes, psp.VolumeBlocks, psp.DiscId, psp.DiscVersion) : null));
}
