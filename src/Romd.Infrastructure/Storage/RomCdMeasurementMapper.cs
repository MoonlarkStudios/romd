using Moonlark.Libchdr;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Cd;
using Romd.Media.Chd;

namespace Romd.Infrastructure.Storage;

/// <summary>Maps complete independently compared originals and exact measured container observations into the fact publication contract.</summary>
public static class RomCdMeasurementMapper
{
    public static RomMeasuredRepresentation Map(ChdCdInspection inspection, ChdCdVerification verification, ChdNativeIdentity native,
        string verifierBuild, DateTimeOffset measuredAt)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(native);
        if (inspection.Version != 5 || inspection.HasParent || inspection.Tracks is null || inspection.Metadata is null || inspection.Profile != ChdCdInspectionService.Profile || inspection.ProjectionProfile != ChdCdInspectionService.ProjectionProfile ||
            verification.Profile is not (ChdCdVerification.CurrentProfile or ChdCdRetainedRequest.CurrentProfile) ||
            (verification.Profile == ChdCdRetainedRequest.CurrentProfile || verification.ObservedBoot.Profile is not null) &&
            verification.ObservedBoot != inspection.ObservedBoot ||
            verification.Members is null || verification.Members.Count is < 2 or > 116 || inspection.Metadata.Count != inspection.Tracks.Count)
            throw new InvalidDataException("CD fact publication requires complete independent original comparisons and exact measured metadata.");
        var header = inspection.Header;
        var container = new RomContainerFacts.Chd(inspection.Version, checked((long)header.LogicalBytes), header.HunkBytes, header.UnitBytes, header.HunkCount,
            [header.Codec0, header.Codec1, header.Codec2, header.Codec3], inspection.HasParent, Sha1.Parse(inspection.StoredRawSha1), Sha1.Parse(inspection.StoredOverallSha1),
            inspection.Metadata.Select(entry => new RomChdMetadataEntry(entry.Tag, entry.Flags, Convert.FromHexString(entry.PayloadHex))).ToArray());
        var tracks = inspection.Tracks.Select(track => new RomCdTrack(checked((int)track.Number), checked((long)track.FirstFrame), checked((int)track.Frames), track.Type switch
        {
            ChdCdTrackType.Mode1Raw => RomCdTrackKind.Mode1Raw,
            ChdCdTrackType.Mode2Raw => RomCdTrackKind.Mode2Raw,
            ChdCdTrackType.Audio => RomCdTrackKind.Audio,
            _ => throw new InvalidDataException("Unsupported measured CD track layout.")
        })).ToArray();
        var members = verification.Members.Select<ChdCdMemberVerification, RomCdFileMember>(member => member.Role switch
        {
            "bin" when member.Reconstructed is not null => new RomCdBinMember(member.Name, Identity(member.Original), Identity(member.Reconstructed), member.Tracks),
            "cue" when member.Reconstructed is null && member.Tracks.Count == 0 => new RomCdStoredMember(member.Name, Identity(member.Original), RomCdStoredRole.Cue),
            "sidecar" when member.Reconstructed is null && member.Tracks.Count == 0 => new RomCdStoredMember(member.Name, Identity(member.Original), RomCdStoredRole.Sidecar),
            _ => throw new InvalidDataException("Each measured original requires its exact reconstruction role.")
        }).ToArray();
        var manifest = new RomReconstructionManifest.CdTracks(checked((long)header.LogicalBytes / 2448), tracks, members);
        var outputs = members.Select((member, ordinal) => (member, ordinal)).Where(pair => pair.member is RomCdBinMember).Select(pair =>
            new RomMeasuredOutput(new RomOutputSelector.Member(pair.ordinal), new(pair.member.Identity.Size, pair.member.Identity.Sha256,
                pair.member.Identity.Sha1, pair.member.Identity.Md5, pair.member.Identity.Crc32))).ToArray();
        var proof = new RomRepresentationVerification(Guid.NewGuid(), Sha256.Parse(inspection.ArtifactSha256), header.ArtifactBytes,
            new RomDecoderIdentity.Native(ChdBuildEvidence.Decoder(native)), verification.Profile, verifierBuild, measuredAt, outputs, container,
            Sha1.Parse(inspection.ComputedRawSha1), Sha1.Parse(inspection.ComputedOverallSha1));
        return new(container, manifest, proof);
    }
    internal static RomCdInspection MapInspection(ITempFile source, ChdCdInspection inspection, ChdNativeIdentity native,
        string verifierBuild, DateTimeOffset measuredAt)
    {
        if (inspection.Profile != ChdCdInspectionService.Profile || inspection.ProjectionProfile != ChdCdInspectionService.ProjectionProfile ||
            inspection.Tracks is null || inspection.Metadata is null || source.DatHashes is not { } hashes ||
            inspection.ArtifactSha256 != source.FileSha256.ToString() || inspection.Header.ArtifactBytes != source.FileSize)
            throw new InvalidDataException("Raw CD observations must bind the complete staged artifact and current inspection profile.");
        var header = inspection.Header;
        var container = new RomContainerFacts.Chd(inspection.Version, checked((long)header.LogicalBytes), header.HunkBytes, header.UnitBytes, header.HunkCount,
            [header.Codec0, header.Codec1, header.Codec2, header.Codec3], inspection.HasParent, Sha1.Parse(inspection.StoredRawSha1), Sha1.Parse(inspection.StoredOverallSha1),
            inspection.Metadata.Select(entry => new RomChdMetadataEntry(entry.Tag, entry.Flags, Convert.FromHexString(entry.PayloadHex))).ToArray());
        var tracks = inspection.Tracks.Select(track => new RomCdInspectedTrack(
            new(checked((int)track.Number), checked((long)track.FirstFrame), checked((int)track.Frames), track.Type switch
            {
                ChdCdTrackType.Mode1Raw => RomCdTrackKind.Mode1Raw,
                ChdCdTrackType.Mode2Raw => RomCdTrackKind.Mode2Raw,
                ChdCdTrackType.Audio => RomCdTrackKind.Audio,
                _ => throw new InvalidDataException("Unsupported raw CD track mode.")
            }), checked((int)track.AlignmentFrames), checked((int)track.PregapFrames), track.PregapStored, checked((int)track.PostgapFrames),
            new(track.BinBytes, Sha256.Parse(track.Sha256), Sha1.Parse(track.Sha1), Md5.Parse(track.Md5), Crc32.Parse(track.Crc32)))).ToArray();
        return new(Guid.NewGuid(), new(source.FileSize, source.FileSha256, hashes.Sha1, hashes.Md5, hashes.Crc32), container,
            ChdBuildEvidence.Decoder(native), inspection.Profile, inspection.ProjectionProfile, verifierBuild, measuredAt,
            Sha1.Parse(inspection.ComputedRawSha1), Sha1.Parse(inspection.ComputedOverallSha1), tracks,
            MapBoot(inspection.ObservedBoot));
    }
    internal static RomCdBoot? MapBoot(CdBootObservation boot) =>
        boot.Ps1 is { } ps1 ? new RomPs1CdBoot(ps1.Profile, ps1.FirstTrackIndex01, ps1.VolumeBlocks, ps1.RootBlock, ps1.RootBytes,
            ps1.BootPath, ps1.BootBlock, ps1.BootBytes, ps1.EntryPoint, ps1.LoadAddress, ps1.LoadBytes) :
        boot.Saturn is { } saturn ? new RomSaturnCdBoot(saturn.Profile, saturn.FirstTrackIndex01, saturn.ProductCode, saturn.AreaSymbols,
            saturn.InitialProgramBytes, Sha256.Parse(saturn.HeaderSha256)) : null;

    internal static CdBootObservation BootObservation(RomCdBoot? boot) => boot switch
    {
        null => default,
        RomPs1CdBoot ps1 => CdBootObservation.FromWire(new(ps1.Profile, ps1.FirstTrackIndex01, ps1.VolumeBlocks, ps1.RootBlock, ps1.RootBytes, ps1.BootPath,
            ps1.BootBlock, ps1.BootBytes, ps1.EntryPoint, ps1.LoadAddress, ps1.LoadBytes), null),
        RomSaturnCdBoot saturn => CdBootObservation.FromWire(null, new(saturn.Profile, saturn.FirstTrackIndex01, saturn.ProductCode, saturn.AreaSymbols,
            saturn.InitialProgramBytes, saturn.HeaderSha256.ToString())),
        _ => throw new InvalidDataException("Unregistered CD boot observation.")
    };
    private static RomCdFileIdentity Identity(ChdCdFileHashes hashes) => new(hashes.Bytes, Sha256.Parse(hashes.Sha256), Sha1.Parse(hashes.Sha1), Md5.Parse(hashes.Md5), Crc32.Parse(hashes.Crc32));
}
