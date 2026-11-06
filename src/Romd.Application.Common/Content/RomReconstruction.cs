using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

public abstract record RomReconstruction
{
    private RomReconstruction() { }
    public sealed record Singleton : RomReconstruction;
    public sealed record CdMember : RomReconstruction
    {
        public CdMember(RomContentArtifact artifact)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (artifact.Facts.Manifest is not RomReconstructionManifest.CdTracks manifest || artifact.Output.Selector is not RomOutputSelector.Member member ||
                manifest.Members[member.Ordinal] is not RomCdBinMember)
                throw new ArgumentException("CD reconstruction requires its exact BIN output.", nameof(artifact));
            Artifact = artifact;
            Manifest = manifest;
            Ordinal = member.Ordinal;
        }
        public RomContentArtifact Artifact { get; }
        public RomRepresentationFacts Facts => Artifact.Facts;
        public int RepresentationId => Facts.Id;
        public int OutputId => Artifact.OutputId;
        public int Ordinal { get; }
        public RomReconstructionManifest.CdTracks Manifest { get; }
        public bool HasDecoder(RomChdDecoderBuild decoder) => Facts.Verifications.Any(proof => proof.Decoder is RomDecoderIdentity.Native native && native.Build == decoder);
        public bool Matches(RomContentDescriptor content) => Artifact.Output.RomId == content.RomId && Artifact.Output.Identity == content.Identity &&
            content.ArtifactSha256 == Facts.ArtifactSha256 && content.ArtifactSize == Facts.ArtifactSize && content.OutputId == OutputId;
        public RomCdMemberBinding Bind(RomChdDecoderBuild decoder)
        {
            var proof = Facts.Verifications.Single(proof => proof.Decoder is RomDecoderIdentity.Native native && native.Build == decoder);
            return new(RepresentationId, OutputId, Ordinal, Facts.ArtifactSha256, proof.Id, decoder);
        }
    }
}

/// <summary>Exact selected-output binding. Never authorizes the container or a sibling.</summary>
public sealed record RomCdMemberBinding(int RepresentationId, int OutputId, int Ordinal, Sha256 ArtifactSha256, Guid MeasurementId, RomChdDecoderBuild Decoder)
    : RomReadBinding;
