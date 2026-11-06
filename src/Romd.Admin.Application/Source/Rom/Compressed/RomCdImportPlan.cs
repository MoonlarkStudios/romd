using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Compressed;

/// <summary>Immutable publication snapshot. Source and descriptor custody are borrowed from the caller through transaction completion.</summary>
public sealed class RomCdImportPlan
{
    public RomCdImportPlan(PreparedRom.Cd source, RomCdImportBinding binding, GeneratedCdCue cue, PreparedRom.WholeFile? originalCue)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(cue);
        var inspected = source.Inspection;
        var bins = binding.Bins.ToArray();
        if (bins.Length != inspected.Tracks.Count || bins.Where((bin, index) => bin.Track.Number != inspected.Tracks[index].Placement.Number ||
            bin.Track.Identity != inspected.Tracks[index].Identity || bin.Requirement.Size != bin.Track.Identity.Size ||
            bin.Requirement.Sha1 != bin.Track.Identity.Sha1 || bin.Requirement.Md5 != bin.Track.Identity.Md5 || bin.Requirement.Crc32 != bin.Track.Identity.Crc32).Any())
            throw new ArgumentException("Catalog binding must retain every actual measured track identity in order.", nameof(binding));
        bool exact = binding.Cue.Matches(cue.Identity);
        if (exact != (originalCue is not null) || originalCue is not null &&
            (originalCue.Encoding != RomRepresentationEncoding.Identity || originalCue.Verification.Identity != cue.Identity ||
             !ReferenceEquals(originalCue.Artifact, cue.Artifact)))
            throw new ArgumentException("Only exact expected CUE bytes may carry independently verified ordinary identity evidence.", nameof(originalCue));
        Source = source;
        Binding = binding with { Bins = Array.AsReadOnly(bins) };
        Cue = cue;
        OriginalCue = originalCue;
        var members = bins.Select<CatalogCdBinBinding, RomCdFileMember>(bin =>
        {
            var identity = FileIdentity(bin.Track.Identity);
            return new RomCdBinMember(bin.Requirement.Name, identity, identity, [bin.Track.Number]);
        }).Append(new RomCdStoredMember(binding.Cue.Path, FileIdentity(cue.Identity), RomCdStoredRole.Cue)).ToArray();
        var manifest = new RomReconstructionManifest.CdTracks(inspected.Container.LogicalBytes / 2448,
            inspected.Tracks.Select(track => track.Placement).ToArray(), members);
        var proof = new RomRepresentationVerification(inspected.Id, inspected.ArtifactIdentity.Sha256, inspected.ArtifactIdentity.Size,
            new RomDecoderIdentity.Native(inspected.Decoder), inspected.CheckedProfile, inspected.VerifierBuild, inspected.ObservedAt,
            bins.Select((bin, index) => new RomMeasuredOutput(new RomOutputSelector.Member(index), bin.Track.Identity)).ToArray(),
            inspected.Container, inspected.ComputedRawSha1, inspected.ComputedOverallSha1);
        Measured = new(inspected.Container, manifest, proof);
    }
    public PreparedRom.Cd Source { get; }
    public RomCdImportBinding Binding { get; }
    public GeneratedCdCue Cue { get; }
    public RomMeasuredRepresentation Measured { get; }
    public PreparedRom.WholeFile? OriginalCue { get; }
    public int CueOrdinal => Binding.Bins.Count;
    public bool ExactCue => Binding.Cue.Matches(Cue.Identity);
    private static RomCdFileIdentity FileIdentity(RomLogicalIdentity identity) =>
        new(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
}
