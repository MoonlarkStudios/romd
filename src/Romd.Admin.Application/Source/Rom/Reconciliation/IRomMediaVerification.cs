using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Exact retained artifact and requested decoder. The caller must hold durable source custody.</summary>
public sealed record RomMediaVerificationSource
{
    public RomMediaVerificationSource(int representationId, int fileId, Sha256 artifactSha256, long artifactSize,
        RomDecoderIdentity decoder)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(representationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        ArgumentNullException.ThrowIfNull(decoder);
        if (artifactSha256.IsEmpty || artifactSize <= 0)
            throw new ArgumentException("An exact retained artifact is required.");
        RepresentationId = representationId;
        FileId = fileId;
        ArtifactSha256 = artifactSha256;
        ArtifactSize = artifactSize;
        Decoder = decoder;
    }
    public int RepresentationId { get; }
    public int FileId { get; }
    public Sha256 ArtifactSha256 { get; }
    public long ArtifactSize { get; }
    public RomDecoderIdentity Decoder { get; }
}

/// <summary>Independently measured bytes and raw observations, without publication or runtime authority.</summary>
public abstract class RomMediaVerificationMeasurement
{
    private RomMediaVerificationMeasurement(RomMeasuredRepresentation representation, TempFileArtifactMeasurement artifact)
        => (Representation, Artifact) = (representation, artifact);
    public RomMeasuredRepresentation Representation { get; }
    public TempFileArtifactMeasurement Artifact { get; }
    public sealed class Whole(RomMeasuredRepresentation representation, RomLogicalInspection inspection,
        TempFileArtifactMeasurement artifact) : RomMediaVerificationMeasurement(representation, artifact)
    {
        public RomLogicalInspection Inspection { get; } = inspection;
    }
    public sealed class Cd(RomMeasuredRepresentation representation, string inspectorBuild, RomPs1CdBoot? boot,
        TempFileArtifactMeasurement artifact) : RomMediaVerificationMeasurement(representation, artifact)
    {
        public string InspectorBuild { get; } = inspectorBuild;
        public RomPs1CdBoot? Boot { get; } = boot;
    }
}

public interface IRomMediaVerification
{
    Task<ErrorOr<IRomMediaVerificationLease>> AcquireAsync(Guid jobId, RomMediaVerificationSource source,
        CancellationToken ct = default);
}
public interface IRomMediaVerificationLease : IAsyncDisposable
{
    bool ProcessTreeStopped { get; }
    Task<ErrorOr<RomMediaVerificationMeasurement>> VerifyAsync(CancellationToken ct = default);
    Task<ErrorOr<Success>> ReleaseProcessingAsync(CancellationToken ct = default);
}
public static class RomMediaVerificationErrors
{
    public static Error Mismatch => Error.Validation("MediaVerification.SourceMismatch", "Measurement must match the exact retained artifact, layout, outputs and requested decoder.");
    public static Error Unavailable => Error.Conflict("MediaVerification.Unavailable", "The requested retained-media verification is unavailable.");
    public static Error Custody => Error.Conflict("MediaVerification.Custody", "Stopped process custody must be established before releasing media capacity.");
}
