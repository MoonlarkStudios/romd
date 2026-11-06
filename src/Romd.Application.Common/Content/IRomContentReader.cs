using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>A logical ROM and its retained, explicitly selected outputs. This establishes no request authorization.</summary>
public sealed class RomContentDescriptor
{
    public RomContentDescriptor(int romId, int preferredOutputId, RomLogicalIdentity identity, string originalFilename, IReadOnlyList<RomContentArtifact> artifacts,
        IReadOnlyList<RomLogicalInspection> inspections, IReadOnlyList<RomDiscMediaReferenceRecord> references)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(inspections);
        ArgumentNullException.ThrowIfNull(references);
        if (string.IsNullOrWhiteSpace(originalFilename) || originalFilename.Length > 500 || originalFilename.Any(char.IsControl))
            throw new ArgumentException("A logical ROM requires its audited original filename.", nameof(originalFilename));
        OriginalFilename = originalFilename;
        var captured = artifacts.ToArray();
        if (romId <= 0 || preferredOutputId <= 0 || captured.Length == 0 || captured.Any(artifact => artifact is null ||
                artifact.Output.RomId != romId || artifact.Output.Identity != identity) ||
            captured.Select(artifact => artifact.OutputId).Distinct().Count() != captured.Length ||
            captured.Count(artifact => artifact.IsPreferred) != 1 || !captured.Any(artifact => artifact.IsPreferred && artifact.OutputId == preferredOutputId))
            throw new ArgumentException("Content requires exact logical ownership and one owned preferred output.");
        var observed = inspections.ToArray();
        if (observed.Any(row => row is null || row.Identity != identity) ||
            observed.Select(row => (row.Profile, row.InspectorBuild)).Distinct().Count() != observed.Length ||
            observed.Any(row => !row.Prefix.SequenceEqual(observed[0].Prefix)))
            throw new ArgumentException("Logical observations require the exact logical identity and consistent prefixes.", nameof(inspections));
        var assertions = references.ToArray();
        if (assertions.Any(row => row is null || row.LogicalBytes != identity.Size || row.Sha1 != identity.Sha1 || row.Md5 != identity.Md5 || row.Crc32 != identity.Crc32) ||
            assertions.Select(row => row.Id).Distinct().Count() != assertions.Length)
            throw new ArgumentException("References require the complete published logical identity.", nameof(references));
        References = Array.AsReadOnly(assertions);
        Inspections = Array.AsReadOnly(observed);
        RomId = romId;
        OutputId = preferredOutputId;
        Identity = identity;
        Artifacts = Array.AsReadOnly(captured);
        Preferred = captured.Single(artifact => artifact.OutputId == preferredOutputId);
        Reconstruction = Preferred.Facts.Manifest is RomReconstructionManifest.CdTracks
            ? new RomReconstruction.CdMember(Preferred) : new RomReconstruction.Singleton();
    }
    public string OriginalFilename { get; }
    public int RomId { get; }
    public int OutputId { get; }
    public RomLogicalIdentity Identity { get; }
    public IReadOnlyList<RomContentArtifact> Artifacts { get; }
    public IReadOnlyList<RomLogicalInspection> Inspections { get; }
    public IReadOnlyList<RomDiscMediaReferenceRecord> References { get; }
    public RomContentArtifact Preferred { get; }
    public RomReconstruction Reconstruction { get; }

    /// <summary>Whether any retained inspection observed a <typeparamref name="TLayout" /> layout.</summary>
    public bool Observes<TLayout>() where TLayout : RomLayoutObservation => Inspections.Any(row => row.Layout is TLayout);

    /// <summary>
    ///     Whether no retained inspection observed another family's layout. A media kind's claim requires this, so a
    ///     positive observation of one family excludes every other kind without naming it. Content with no layout qualifies.
    /// </summary>
    public bool ObservesNoLayoutOtherThan<TLayout>() where TLayout : RomLayoutObservation =>
        Inspections.All(row => row.Layout is null or TLayout);
    public int RepresentationId => Preferred.RepresentationId;
    public int FileId => Preferred.FileId;
    public Sha256 ContentSha256 => Identity.Sha256;
    public long ContentSize => Identity.Size;
    public Md5 ContentMd5 => Identity.Md5;
    public Sha1 ContentSha1 => Identity.Sha1;
    public Crc32 ContentCrc32 => Identity.Crc32;
    public Sha256 ArtifactSha256 => Preferred.ArtifactSha256;
    public long ArtifactSize => Preferred.ArtifactSize;
    public RomRepresentationEncoding Encoding => Preferred.Encoding;
    public string EncodingProfile => Preferred.EncodingProfile;
}

/// <summary>A selected output of a retained complete fact graph; no cached evidence or canonical flags.</summary>
public sealed class RomContentArtifact
{
    public RomContentArtifact(RomRepresentationFacts facts, int outputId, bool isPreferred, Md5? artifactMd5 = null, Sha1? artifactSha1 = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        Facts = facts;
        Output = facts.Outputs.Single(output => output.Id == outputId);
        IsPreferred = isPreferred;
        ArtifactMd5 = artifactMd5;
        ArtifactSha1 = artifactSha1;
        ChdEvidence = facts.Manifest is RomReconstructionManifest.WholeFile && facts.Container is RomContainerFacts.Chd
            ? facts.Verifications.Where(proof => proof.Decoder is RomDecoderIdentity.Native)
                .Select(proof => new RomChdDeliveryEvidence(facts, outputId, proof.Id)).ToArray() : [];
    }
    public RomRepresentationFacts Facts { get; }
    public RomRepresentationOutput Output { get; }
    public int OutputId => Output.Id;
    public int RepresentationId => Facts.Id;
    public int FileId => Facts.FileId;
    public Sha256 ArtifactSha256 => Facts.ArtifactSha256;
    public long ArtifactSize => Facts.ArtifactSize;
    public RomRepresentationEncoding Encoding => Facts.Container.Encoding;
    public RomContainerFacts Container => Facts.Container;
    public bool IsPreferred { get; }
    public bool HasMeasuredEvidence => Facts.Verifications.Count != 0;
    public Md5? ArtifactMd5 { get; }
    public Sha1? ArtifactSha1 { get; }
    public IReadOnlyList<RomChdDeliveryEvidence> ChdEvidence { get; }
    // A presentation label derived from observed scalars. It is never a canonical or admission decision.
    public string EncodingProfile => MediaParameterLabels.Describe(Container);
    public RomManagedReadBinding BindManaged(string decoderBuild)
    {
        var proof = Facts.Verifications.Single(proof => proof.Decoder is RomDecoderIdentity.Managed managed && managed.Build == decoderBuild);
        return new(RepresentationId, OutputId, proof.Id, decoderBuild);
    }
}

public static class MediaParameterLabels
{
    public static string Describe(RomContainerFacts container) => container switch
    {
        RomContainerFacts.Identity => "identity/v1",
        RomContainerFacts.Rvz rvz => FormattableString.Invariant($"rvz-import/v1;codec={rvz.Compression};level={rvz.CompressionLevel};block={rvz.BlockBytes};format={rvz.Version:x8};encoder=unknown"),
        RomContainerFacts.Chd chd => FormattableString.Invariant($"chd-observed/v1;version={chd.Version};logical={chd.LogicalBytes};hunk={chd.HunkBytes};unit={chd.UnitBytes};count={chd.HunkCount};codecs={chd.Codecs[0]:x8},{chd.Codecs[1]:x8},{chd.Codecs[2]:x8},{chd.Codecs[3]:x8};parent=none;metadata={chd.MetadataSha256}"),
        _ => throw new ArgumentException("Unknown container facts.", nameof(container))
    };
}

/// <summary>One exact whole-file native measurement. CD scope is represented by its selected member instead.</summary>
public sealed class RomChdDeliveryEvidence
{
    public RomChdDeliveryEvidence(RomRepresentationFacts facts, int outputId, Guid verificationId)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Manifest is not RomReconstructionManifest.WholeFile || facts.Container is not RomContainerFacts.Chd container)
            throw new ArgumentException("Whole-file CHD evidence requires a whole-file CHD graph.", nameof(facts));
        var measured = facts.Verifications.Single(proof => proof.Id == verificationId);
        if (measured.Decoder is not RomDecoderIdentity.Native native)
            throw new ArgumentException("Native decoder identity is required.");
        Facts = facts;
        Container = container;
        Measurement = measured;
        Decoder = native.Build;
        Output = facts.Outputs.Single(output => output.Id == outputId);
    }
    public RomRepresentationFacts Facts { get; }
    public RomContainerFacts.Chd Container { get; }
    public RomRepresentationVerification Measurement { get; }
    public RomRepresentationOutput Output { get; }
    public RomChdDecoderBuild Decoder { get; }
    public int RepresentationId => Facts.Id;
    public bool IsValidFor(RomContentDescriptor content, RomContentArtifact artifact) =>
        RepresentationId == artifact.RepresentationId && Output.Id == artifact.OutputId && Output.RomId == content.RomId &&
        Output.Identity == content.Identity && Facts.ArtifactSha256 == artifact.ArtifactSha256 && Facts.ArtifactSize == artifact.ArtifactSize;
    public RomChdDeliveryBinding Bind() => new(RepresentationId, Output.Id, Measurement.Id, Decoder);
    public bool AdmitsTarget(RomDeliveryTarget target, RomContentDescriptor content, RomContentArtifact artifact,
        IRomChdDeliveryAdmission? admission) => IsValidFor(content, artifact) && RomDeliveryTargets.IsValid(target) &&
        target.Encoding == RomRepresentationEncoding.Chd && target.RomId == content.RomId && target.FileId == artifact.FileId && target.ReadBinding == Bind() &&
        target.ContentSha256 == content.ContentSha256 && target.ContentSizeBytes == content.ContentSize &&
        target.ArtifactSha256 == artifact.ArtifactSha256 && target.ArtifactSizeBytes == artifact.ArtifactSize &&
        (target.Format == RomDeliveryFormat.Original ? target.Md5 == content.ContentMd5 && target.Sha1 == content.ContentSha1
            : target.Md5 == Known(artifact.ArtifactMd5) && target.Sha1 == Known(artifact.ArtifactSha1)) &&
        admission?.Assess(this) is { } assessment && assessment.Matches(this);
    private static T? Known<T>(T? hash) where T : struct, IHashValue<T> => hash is { IsEmpty: false } ? hash : null;
}

public abstract record RomReadBinding
{
    private protected RomReadBinding() { }
}
public sealed record RomChdDeliveryBinding(int RepresentationId, int OutputId, Guid MeasurementId, RomChdDecoderBuild Decoder)
    : RomReadBinding;

public sealed record RomManagedReadBinding(int RepresentationId, int OutputId, Guid MeasurementId, string DecoderBuild) : RomReadBinding;

/// <summary>Serving authority; native assessments re-verify the deployed build and invalidate readiness on failure.</summary>
public interface IRomChdDeliveryAdmission
{
    RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence);
    RomManagedReadBinding? AssessManaged(RomContentArtifact artifact);
    bool AdmitsCd(RomReconstruction.CdMember member);
    RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member);
}
public sealed record RomChdDeliveryAssessment(RomChdDecoderBuild Decoder)
{
    public bool Matches(RomChdDeliveryEvidence evidence) => Decoder == evidence.Decoder;
}

public interface IRomContentCatalog
{
    const int MaximumBatchSize = 256;

    Task<RomContentDescriptor?> FindAsync(Sha256 contentSha256, CancellationToken ct = default);
    Task<RomContentDescriptor?> FindByRomIdAsync(int romId, CancellationToken ct = default);

    /// <summary>Reads at most MaximumBatchSize ROM ids; missing ids are omitted. Not an authorization grant.</summary>
    Task<IReadOnlyDictionary<int, RomContentDescriptor>> FindByRomIdsAsync(
        IReadOnlyCollection<int> romIds, CancellationToken ct = default);
}

/// <summary>
/// Reads bound format targets or logical bytes after the caller has authorized access. Generic file storage reads artifact bytes.
/// No encoding or temporary disc creation occurs here. The caller owns the returned stream.
/// </summary>
public interface IRomContentReader
{
    /// <summary>Opens the exact target; consuming to EOF verifies its delivered SHA-256.</summary>
    Task<Stream?> OpenAsync(RomDeliveryTarget target, CancellationToken ct = default, TimeSpan? admissionTimeout = null);
    /// <summary>Seekable exact-target reader; individual ranges are not cryptographically authenticated.</summary>
    Task<Stream?> OpenSeekableAsync(RomDeliveryTarget target, CancellationToken ct = default);
    /// <summary>Seekable range reader; individual ranges are not cryptographically authenticated.</summary>
    Task<Stream?> OpenAsync(Sha256 contentSha256, CancellationToken ct = default);
    /// <summary>Sequential reader; consuming to EOF verifies the logical SHA-256.</summary>
    /// <param name="admissionTimeout">Maximum wait for shared decoder capacity; null uses its bounded default, zero rejects immediately.</param>
    Task<Stream?> OpenVerifiedByRomIdAsync(int romId, CancellationToken ct = default, TimeSpan? admissionTimeout = null);
}
