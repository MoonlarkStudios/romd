using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     Admits CHD reads only through retained evidence for the exact artifact and the host's serving authority, which
///     re-assesses the deployed decoder on every read; no decision is reused. Original reads open synchronous raw
///     content, never compressed access.
/// </summary>
internal static class ChdReadAdmission
{
    /// <summary>The name of the probe target an Original read is admitted through; admission compares bound identity, never names.</summary>
    private const string _originalProbeName = "original";

    internal static RomReadAdmission Create(IRomChdDeliveryAdmission? delivery) => new(
        content => AdmitOriginal(content, delivery),
        (target, current) => (current.Artifacts ?? []).Any(artifact => (artifact.ChdEvidence ?? []).Any(evidence =>
            evidence is not null && evidence.AdmitsTarget(target, current, artifact, delivery))));

    // Evidence is tried in integrity order; the first the serving authority admits for the current artifact wins.
    private static RomOriginalReadDecision? AdmitOriginal(RomContentDescriptor content, IRomChdDeliveryAdmission? delivery)
    {
        var admitted = (content.Artifacts ?? [])
            .Where(artifact => IsCurrentArtifact(artifact, content))
            .SelectMany(artifact => (artifact.ChdEvidence ?? [])
                .Where(evidence => evidence is not null && evidence.IsValidFor(content, artifact))
                .OrderBy(evidence => evidence.Measurement.Id)
                .Where(evidence => evidence.AdmitsTarget(CurrentOriginal(content, artifact, evidence), content, artifact, delivery)))
            .FirstOrDefault();
        return admitted is null ? null : RomOriginalReadDecision.SynchronousRaw(admitted);
    }

    private static bool IsCurrentArtifact(RomContentArtifact artifact, RomContentDescriptor content) =>
        artifact.Encoding == RomRepresentationEncoding.Chd && artifact.FileId == content.FileId &&
        artifact.RepresentationId == content.RepresentationId && artifact.ArtifactSha256 == content.ArtifactSha256 &&
        artifact.ArtifactSize == content.ArtifactSize && artifact.EncodingProfile == content.EncodingProfile;

    private static RomDeliveryTarget CurrentOriginal(RomContentDescriptor content, RomContentArtifact artifact,
        RomChdDeliveryEvidence evidence) => new(content.RomId, artifact.FileId, RomDeliveryFormat.Original,
        _originalProbeName, content.ContentSha256, content.ContentSize, content.ContentSha256, content.ContentSize,
        artifact.ArtifactSha256, artifact.ArtifactSize, artifact.Encoding, content.ContentMd5, content.ContentSha1,
        evidence.Bind());
}
