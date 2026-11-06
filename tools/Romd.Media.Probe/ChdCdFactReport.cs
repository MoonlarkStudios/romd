using System.Text.Json;
using Romd.Domain.Source.Rom;

namespace Romd.Media.Probe;

/// <summary>Private probe evidence captures typed measurements without using an application persistence format.</summary>
internal static class ChdCdFactReport
{
    internal static JsonElement Create(RomMeasuredRepresentation measured)
    {
        if (measured.Container is not RomContainerFacts.Chd header || measured.Manifest is not RomReconstructionManifest.CdTracks manifest)
            throw new ArgumentException("CD probe reporting requires complete measured CHD facts.", nameof(measured));
        var proof = measured.Measurement;
        return JsonSerializer.SerializeToElement(new
        {
            Schema = "measured-cd-probe/v1",
            ArtifactSha256 = proof.ArtifactSha256.ToString(),
            proof.ArtifactSize,
            Container = new
            {
                header.Version,
                header.LogicalBytes,
                header.HunkBytes,
                header.UnitBytes,
                header.HunkCount,
                header.Codecs,
                header.HasParent,
                StoredRawSha1 = header.StoredRawSha1.ToString(),
                StoredOverallSha1 = header.StoredOverallSha1.ToString(),
                ParametersSha256 = header.Fingerprint.ToString(),
                MetadataSha256 = header.MetadataSha256.ToString(),
                Metadata = header.Metadata.Select(entry => new { entry.Tag, entry.Flags, PayloadHex = Convert.ToHexStringLower(entry.Payload) })
            },
            Manifest = new
            {
                manifest.LogicalFrames,
                manifest.Tracks,
                Members = manifest.Members.Select(member => new
                {
                    member.Path,
                    member.Identity.Size,
                    Sha256 = member.Identity.Sha256.ToString(),
                    Sha1 = member.Identity.Sha1.ToString(),
                    Md5 = member.Identity.Md5.ToString(),
                    Crc32 = member.Identity.Crc32.ToString(),
                    Tracks = (member as RomCdBinMember)?.Tracks,
                    StoredRole = (member as RomCdStoredMember)?.Role.ToString()
                })
            },
            Measurement = new
            {
                proof.Id,
                Decoder = ((RomDecoderIdentity.Native)proof.Decoder).Build.BuildLabel,
                proof.CheckedProfile,
                proof.VerifierBuild,
                proof.ObservedAt,
                ComputedRawSha1 = proof.ComputedRawSha1.ToString(),
                ComputedOverallSha1 = proof.ComputedOverallSha1.ToString(),
                Outputs = proof.Outputs.Select(output => new
                {
                    MemberOrdinal = ((RomOutputSelector.Member)output.Selector).Ordinal,
                    output.Identity.Size,
                    Sha256 = output.Identity.Sha256.ToString(),
                    Sha1 = output.Identity.Sha1.ToString(),
                    Md5 = output.Identity.Md5.ToString(),
                    Crc32 = output.Identity.Crc32.ToString()
                })
            }
        }, JsonOptions.Value);
    }
}
