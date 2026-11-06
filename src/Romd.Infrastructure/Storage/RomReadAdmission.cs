using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     Reader-side admission for one encoding, decided before storage opens. A decision names the artifact access to
///     open and never opens it: content store access stays in <see cref="RomContentReader" />.
/// </summary>
/// <param name="Original">
///     Admits an Original read of the ROM's current stored representation, or returns null to refuse it. Throws
///     <see cref="InvalidDataException" /> when the representation contradicts its logical identity.
/// </param>
/// <param name="StoredTarget">
///     Rechecks a bound stored-format target against the ROM's current retained facts; null when the encoding's stored
///     targets are served from their bound artifact without reading the catalog.
/// </param>
internal sealed record RomReadAdmission(
    Func<RomContentDescriptor, RomOriginalReadDecision?> Original,
    Func<RomDeliveryTarget, RomContentDescriptor, bool>? StoredTarget = null)
{
    internal static RomReadAdmission Managed(IRomChdDeliveryAdmission? admission) => new(
        content => admission?.AssessManaged(content.Preferred) is not null ? RomOriginalReadDecision.RandomAccess : null,
        (target, content) => content.Artifacts.Any(artifact =>
            artifact.FileId == target.FileId && artifact.ArtifactSha256 == target.ArtifactSha256 && artifact.ArtifactSize == target.ArtifactSizeBytes &&
            artifact.Output.Identity == content.Identity && target.ContentSha256 == content.ContentSha256 && target.ContentSizeBytes == content.ContentSize &&
            target.ReadBinding is RomManagedReadBinding binding && admission?.AssessManaged(artifact) == binding &&
            (target.Format == RomDeliveryFormat.Original ? target.Md5 == content.ContentMd5 && target.Sha1 == content.ContentSha1
                : target.Md5 == artifact.ArtifactMd5 && target.Sha1 == artifact.ArtifactSha1)));

    /// <summary>An Identity artifact is its logical content, so any disagreement between the two is corruption.</summary>
    internal static RomReadAdmission Identity { get; } = new(content =>
        content.ContentSha256 != content.ArtifactSha256 || content.ContentSize != content.ArtifactSize
            ? throw new InvalidDataException("Original representation disagrees with logical identity.")
            : content.Preferred.HasMeasuredEvidence ? RomOriginalReadDecision.RandomAccess : null);
}

/// <summary>The one access an opened artifact snapshot offers its reader adapter.</summary>
internal enum RomArtifactAccess
{
    RandomAccess,
    SynchronousRaw
}

/// <summary>An admitted Original read: the artifact access to open and the retained facts the reader admitted.</summary>
internal sealed class RomOriginalReadDecision
{
    private RomOriginalReadDecision(RomArtifactAccess access, RomChdDeliveryEvidence? chdEvidence) =>
        (Access, ChdEvidence) = (access, chdEvidence);

    internal static RomOriginalReadDecision RandomAccess { get; } = new(RomArtifactAccess.RandomAccess, null);

    internal RomArtifactAccess Access { get; }

    /// <summary>Retained CHD evidence admitted for this exact artifact; random access never carries evidence.</summary>
    internal RomChdDeliveryEvidence? ChdEvidence { get; }

    internal static RomOriginalReadDecision SynchronousRaw(RomChdDeliveryEvidence evidence) =>
        new(RomArtifactAccess.SynchronousRaw, evidence ?? throw new ArgumentNullException(nameof(evidence)));
}

/// <param name="ChdDelivery">The host's CHD serving authority; null refuses every CHD read.</param>
internal sealed record RomReadAdmissionDependencies(IRomChdDeliveryAdmission? ChdDelivery);
