using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>Plans retained formats from recorded facts, without decoding or inferring preparation eligibility.</summary>
public static class RomDeliveryPlanner
{
    /// <summary>
    ///     Offers original and each encoding the content holds; bytes are admitted from retained evidence and the serving
    ///     authority alone. Runtime claims come only from the content's uniquely resolved <paramref name="kind" />, so any
    ///     other resolution, or none, claims no runtime.
    /// </summary>
    public static IReadOnlyList<RomDeliveryOption> Plan(RomContentDescriptor content, string filename,
        RomDeliveryPolicy deliveryPolicy, MediaKindResolution? kind = null, IRomChdDeliveryAdmission? chdAdmission = null)
    {
        if (content.Reconstruction is RomReconstruction.CdMember member)
        {
            // A single-member plan can expose only that exact original. Whole-CHD planning
            // belongs to the release/file-set lane after authorization of every output.
            string name = filename.Replace('\\', '/').Split('/').Last();
            var decoder = member.Matches(content) ? chdAdmission?.AssessCd(member) : null;
            return [decoder is not null
                ? new(RomDeliveryFormat.Original, RomDeliveryState.Available,
                    new(content.RomId, content.FileId, RomDeliveryFormat.Original, name,
                        content.ContentSha256, content.ContentSize, content.ContentSha256, content.ContentSize,
                        content.ArtifactSha256, content.ArtifactSize, content.Encoding,
                        content.ContentMd5, content.ContentSha1, member.Bind(decoder)),
                    member.Facts.Verifications.Single(proof => proof.Decoder is RomDecoderIdentity.Native native && native.Build == decoder).CheckedProfile, [])
                : Unsupported(RomDeliveryFormat.Original)];
        }
        var chdCandidates = AdmittedChd(content, chdAdmission);
        var preferredChd = content.Encoding == RomRepresentationEncoding.Chd
            ? chdCandidates.FirstOrDefault(candidate => candidate.Artifact.RepresentationId == content.RepresentationId
                && candidate.Artifact.FileId == content.FileId && candidate.Artifact.ArtifactSha256 == content.ArtifactSha256
                && candidate.Artifact.ArtifactSize == content.ArtifactSize)
            : null;
        string originalName = filename.Replace('\\', '/').Split('/').Last();
        if (deliveryPolicy.OriginalExtension(content.Encoding) is { } originalExtension &&
            ((content.Encoding == RomRepresentationEncoding.Rvz &&
              string.Equals(Path.GetExtension(originalName), deliveryPolicy.Formats.FileExtension(RomDeliveryFormat.Rvz),
                  StringComparison.OrdinalIgnoreCase)) ||
             (content.Encoding == RomRepresentationEncoding.Chd && preferredChd is not null &&
              string.Equals(Path.GetExtension(originalName), deliveryPolicy.Formats.FileExtension(RomDeliveryFormat.Chd),
                  StringComparison.OrdinalIgnoreCase))))
            originalName = Path.ChangeExtension(originalName, originalExtension);
        var managedBinding = content.Encoding == RomRepresentationEncoding.Rvz ? chdAdmission?.AssessManaged(content.Preferred) : null;
        bool admitsOriginal = content.Encoding == RomRepresentationEncoding.Identity && content.Preferred.HasMeasuredEvidence ||
            content.Encoding == RomRepresentationEncoding.Rvz && managedBinding is not null ||
            content.Encoding == RomRepresentationEncoding.Chd && preferredChd is not null;
        var original = admitsOriginal
            ? new RomDeliveryOption(RomDeliveryFormat.Original, RomDeliveryState.Available,
                new(content.RomId, content.FileId, RomDeliveryFormat.Original, originalName,
                    content.ContentSha256, content.ContentSize, content.ContentSha256, content.ContentSize,
                    content.ArtifactSha256, content.ArtifactSize, content.Encoding,
                    content.ContentMd5, content.ContentSha1, (RomReadBinding?)preferredChd?.Evidence.Bind() ?? managedBinding),
                deliveryPolicy.ProfileVersion(content.EncodingProfile), [])
            : Unsupported(RomDeliveryFormat.Original);
        var rvz = content.Artifacts.Where(artifact => artifact.Encoding == RomRepresentationEncoding.Rvz)
            .Select(artifact => new { Artifact = artifact, Binding = chdAdmission?.AssessManaged(artifact) }).Where(candidate => candidate.Binding is not null)
            .OrderByDescending(candidate => candidate.Artifact.IsPreferred)
            .ThenBy(candidate => candidate.Artifact.ArtifactSha256.ToString(), StringComparer.Ordinal).FirstOrDefault();
        // Encoded formats are offered only where the ROM holds that encoding; original always comes first.
        var options = new List<RomDeliveryOption>(3) { original };
        if (Holds(content, RomRepresentationEncoding.Rvz))
            options.Add(rvz is null
                ? Unsupported(RomDeliveryFormat.Rvz)
                : new RomDeliveryOption(RomDeliveryFormat.Rvz, RomDeliveryState.Available,
                    new(content.RomId, rvz.Artifact.FileId, RomDeliveryFormat.Rvz,
                        Path.ChangeExtension(originalName, deliveryPolicy.Formats.FileExtension(RomDeliveryFormat.Rvz)),
                        rvz.Artifact.ArtifactSha256, rvz.Artifact.ArtifactSize, content.ContentSha256, content.ContentSize,
                        rvz.Artifact.ArtifactSha256, rvz.Artifact.ArtifactSize, rvz.Artifact.Encoding,
                        Known(rvz.Artifact.ArtifactMd5), Known(rvz.Artifact.ArtifactSha1), rvz.Binding), deliveryPolicy.ProfileVersion(rvz.Artifact.EncodingProfile),
                    kind?.NativeRuntimes(RomDeliveryFormat.Rvz) ?? []));
        if (!Holds(content, RomRepresentationEncoding.Chd))
            return [.. options];
        var chd = chdCandidates.FirstOrDefault(candidate => candidate.Artifact.ArtifactMd5 is { IsEmpty: false }
            && candidate.Artifact.ArtifactSha1 is { IsEmpty: false });
        options.Add(chd is null
            ? Unsupported(RomDeliveryFormat.Chd)
            : new RomDeliveryOption(RomDeliveryFormat.Chd, RomDeliveryState.Available,
                new(content.RomId, chd.Artifact.FileId, RomDeliveryFormat.Chd,
                    Path.ChangeExtension(originalName, deliveryPolicy.Formats.FileExtension(RomDeliveryFormat.Chd)),
                    chd.Artifact.ArtifactSha256, chd.Artifact.ArtifactSize, content.ContentSha256, content.ContentSize,
                    chd.Artifact.ArtifactSha256, chd.Artifact.ArtifactSize, chd.Artifact.Encoding,
                    Known(chd.Artifact.ArtifactMd5), Known(chd.Artifact.ArtifactSha1), chd.Evidence.Bind()),
                deliveryPolicy.ProfileVersion(chd.Artifact.EncodingProfile),
                kind?.NativeRuntimes(RomDeliveryFormat.Chd) ?? []));
        return [.. options];
    }

    private static bool Holds(RomContentDescriptor content, RomRepresentationEncoding encoding) =>
        content.Encoding == encoding || content.Artifacts?.Any(artifact => artifact.Encoding == encoding) == true;

    private static IReadOnlyList<ChdCandidate> AdmittedChd(RomContentDescriptor content, IRomChdDeliveryAdmission? admission)
    {
        var candidates = new List<ChdCandidate>();
        foreach (var artifact in content.Artifacts ?? [])
        {
            foreach (var evidence in artifact.ChdEvidence ?? [])
            {
                if (evidence.IsValidFor(content, artifact) && admission?.Assess(evidence) is { } assessment
                    && assessment.Matches(evidence))
                    candidates.Add(new(artifact, evidence));
            }
        }
        return candidates.OrderByDescending(candidate => candidate.Artifact.IsPreferred)
            .ThenBy(candidate => candidate.Artifact.ArtifactSha256.ToString(), StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Artifact.FileId).ThenBy(candidate => candidate.Artifact.RepresentationId)
            .ThenBy(candidate => candidate.Evidence.Measurement.Id).ToArray();
    }

    private sealed record ChdCandidate(RomContentArtifact Artifact, RomChdDeliveryEvidence Evidence);

    private static RomDeliveryOption Unsupported(RomDeliveryFormat format) =>
        new(format, RomDeliveryState.Unsupported, null, "unknown", []);

    private static T? Known<T>(T? hash) where T : struct, IHashValue<T> => hash is { IsEmpty: false } ? hash : null;
}
