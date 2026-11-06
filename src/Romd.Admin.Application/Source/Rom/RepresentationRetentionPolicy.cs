using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

public enum RepresentationRetention { Retain, EvidenceOnly, Discard }

public sealed record RetainedRepresentation(int RepresentationId, int OutputId, int FileId, Sha256 ArtifactSha256,
    RomContainerFacts Container, bool IsPreferred, bool HasMeasuredEvidence)
{
    public RomRepresentationEncoding Encoding => Container.Encoding;
}

public sealed record RepresentationRetentionDecision(RepresentationRetention Retention,
    Sha256 PreferredArtifact, IReadOnlyList<RetainedRepresentation> Release);

/// <summary>Pure ranking over measured physical parameters and the current registered recipe.</summary>
public static class RepresentationRetentionPolicy
{
    public static RepresentationRetentionDecision Decide(IReadOnlyList<RetainedRepresentation> existing,
        PreparedRom.WholeFile prepared, RomMediaPolicyRegistry policies, RomIsoDiscKind? kind = null)
    {
        var preferred = existing.SingleOrDefault(row => row.IsPreferred);
        var exact = existing.Where(row => row.ArtifactSha256 == prepared.Artifact.FileSha256).OrderByDescending(row => row.IsPreferred).ThenBy(row => row.OutputId).FirstOrDefault();
        int incomingRank = Rank(prepared.Container, true, policies, kind);
        int strongestRank = existing.Count == 0 ? 0 : existing.Max(row => Rank(row.Container, row.HasMeasuredEvidence, policies, kind));
        if (exact is null && existing.Count > 0 && incomingRank <= strongestRank)
            return new(RepresentationRetention.Discard, preferred!.ArtifactSha256, []);

        bool promote = incomingRank >= strongestRank;
        return new(exact is null ? RepresentationRetention.Retain : RepresentationRetention.EvidenceOnly,
            promote ? prepared.Artifact.FileSha256 : preferred!.ArtifactSha256,
            promote ? existing.Where(row => row.ArtifactSha256 != prepared.Artifact.FileSha256 &&
                Rank(row.Container, row.HasMeasuredEvidence, policies, kind) < incomingRank).ToArray() : []);
    }

    private static int Rank(RomContainerFacts container, bool measured,
        RomMediaPolicyRegistry policies, RomIsoDiscKind? kind) => container.Encoding switch
        {
            RomRepresentationEncoding.Identity => 1,
            var encoded when measured && encoded.IsEncoded() =>
                policies.Classify(container, kind) == RomRepresentationProfileClass.Canonical ? 3 : 2,
            _ => 0
        };
}
