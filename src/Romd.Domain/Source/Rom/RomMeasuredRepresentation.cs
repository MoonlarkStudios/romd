namespace Romd.Domain.Source.Rom;

/// <summary>A complete independent measurement before durable representation/output IDs are allocated.</summary>
public sealed class RomMeasuredRepresentation
{
    public RomMeasuredRepresentation(RomContainerFacts container, RomReconstructionManifest manifest, RomRepresentationVerification measurement)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.ObservedParametersSha256 != container.Fingerprint ||
            (container is RomContainerFacts.Chd) != (measurement.Decoder is RomDecoderIdentity.Native))
            throw new ArgumentException("Measured container facts must match the complete independent verification.");
        if (manifest is RomReconstructionManifest.WholeFile)
        {
            var output = measurement.Outputs.Count == 1 ? measurement.Outputs[0] : null;
            if (output?.Selector is not RomOutputSelector.Whole || container switch
            {
                RomContainerFacts.Identity => measurement.ArtifactSha256 != output.Identity.Sha256 || measurement.ArtifactSize != output.Identity.Size,
                RomContainerFacts.Rvz rvz => rvz.LogicalBytes != output.Identity.Size,
                RomContainerFacts.Chd chd => chd.LogicalBytes != output.Identity.Size || chd.StoredRawSha1 != output.Identity.Sha1,
                _ => true
            })
                throw new ArgumentException("Whole-file measurement requires one exact complete logical output.");
        }
        else if (manifest is RomReconstructionManifest.CdTracks cd)
        {
            if (container is not RomContainerFacts.Chd chd || chd.UnitBytes != 2448 || chd.LogicalBytes != cd.LogicalFrames * 2448 || chd.Metadata.Count == 0 ||
                measurement.Outputs.Count != cd.Members.OfType<RomCdBinMember>().Count() || measurement.Outputs.Any(output =>
                    output.Selector is not RomOutputSelector.Member member || !cd.ContainsOutput(member) ||
                    !Same(output.Identity, cd.Members[member.Ordinal].Identity)))
                throw new ArgumentException("A CD measurement must independently measure every exact original BIN output.");
        }
        else
            throw new ArgumentException("Unknown reconstruction manifest.", nameof(manifest));
        if (container is RomContainerFacts.Chd header && (measurement.ArtifactSize < 124 ||
            header.Metadata.Sum(entry => (long)entry.Payload.Length + 16) + 124 > measurement.ArtifactSize ||
            measurement.ComputedRawSha1 != header.StoredRawSha1 || measurement.ComputedOverallSha1 != header.StoredOverallSha1))
            throw new ArgumentException("Measured CHD integrity and metadata must describe the exact complete artifact.");
        Container = container;
        Manifest = manifest;
        Measurement = measurement;
    }
    public RomContainerFacts Container { get; }
    public RomReconstructionManifest Manifest { get; }
    public RomRepresentationVerification Measurement { get; }
    public bool MatchesRetained(RomRepresentationFacts retained) => Measurement.ArtifactSha256 == retained.ArtifactSha256 &&
        Measurement.ArtifactSize == retained.ArtifactSize && Container.Encoding == retained.Container.Encoding && Container.Fingerprint == retained.Container.Fingerprint && MatchesLayout(retained);
    /// <summary>Exact reconstruction layout and file identities, independently of compressed artifact bytes or decoder evidence.</summary>
    public bool MatchesLayout(RomRepresentationFacts retained) => Measurement.Outputs.Count == retained.Outputs.Count && Measurement.Outputs.All(measured => retained.Outputs.Any(output =>
            output.Selector == measured.Selector && output.Identity == measured.Identity)) && (Manifest, retained.Manifest) switch
            {
                (RomReconstructionManifest.WholeFile, RomReconstructionManifest.WholeFile) => true,
                (RomReconstructionManifest.CdTracks left, RomReconstructionManifest.CdTracks right) => left.LogicalFrames == right.LogicalFrames &&
                    left.Tracks.SequenceEqual(right.Tracks) && left.Members.Count == right.Members.Count && left.Members.Select((member, index) =>
                        SameMember(member, right.Members[index])).All(equal => equal),
                _ => false
            };
    private static bool SameMember(RomCdFileMember left, RomCdFileMember right) => left.Path == right.Path && left.Identity == right.Identity && (left, right) switch
    {
        (RomCdBinMember first, RomCdBinMember second) => first.Tracks.SequenceEqual(second.Tracks),
        (RomCdStoredMember first, RomCdStoredMember second) => first.Role == second.Role,
        _ => false
    };
    private static bool Same(RomLogicalIdentity logical, RomCdFileIdentity member) => logical.Size == member.Size && logical.Sha256 == member.Sha256 &&
        logical.Sha1 == member.Sha1 && logical.Md5 == member.Md5 && logical.Crc32 == member.Crc32;
}
