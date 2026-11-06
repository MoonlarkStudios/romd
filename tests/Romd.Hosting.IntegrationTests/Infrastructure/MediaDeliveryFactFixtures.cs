using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>Complete in-memory graph selections for endpoint tests; no admission or persistence side effects.</summary>
internal static class MediaDeliveryFactFixtures
{
    public static RomContentArtifact MeasuredWhole(int representationId, int fileId, int outputId, int ownerRomId,
        bool preferred, RomContainerFacts container, RomRepresentationVerification measurement, Md5? artifactMd5, Sha1? artifactSha1)
    {
        var identity = measurement.Outputs.Single(output => output.Selector is RomOutputSelector.Whole).Identity;
        return new(new(representationId, fileId, measurement.ArtifactSha256, measurement.ArtifactSize, container,
            RomReconstructionManifest.WholeFile.Instance, [new(outputId, ownerRomId, RomOutputSelector.Whole.Instance, identity)],
            [measurement], []), outputId, preferred, artifactMd5, artifactSha1);
    }

    public static RomContentArtifact UnmeasuredWhole(int representationId, int fileId, int outputId, int ownerRomId,
        bool preferred, RomLogicalIdentity identity, Sha256 artifactSha256, long artifactBytes, RomContainerFacts container,
        Md5? artifactMd5, Sha1? artifactSha1) => new(new(representationId, fileId, artifactSha256, artifactBytes, container,
            RomReconstructionManifest.WholeFile.Instance, [new(outputId, ownerRomId, RomOutputSelector.Whole.Instance, identity)],
            [], []), outputId, preferred, artifactMd5, artifactSha1);
}

internal sealed class ManagedMediaTestAdmission(string decoderBuild) : IRomChdDeliveryAdmission
{
    public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => null;
    public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => artifact.Container is RomContainerFacts.Rvz &&
        artifact.Facts.Verifications.Any(proof => proof.Decoder is RomDecoderIdentity.Managed managed && managed.Build == decoderBuild)
            ? artifact.BindManaged(decoderBuild) : null;
    public bool AdmitsCd(RomReconstruction.CdMember member) => false;
    public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => null;
}
