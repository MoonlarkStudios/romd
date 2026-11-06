using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>One proposed physical operation. Capture checks it against current demand and exact source facts before admission.</summary>
public sealed class MediaReconciliationCapture
{
    public MediaReconciliationCapture(Guid jobId, long requestedVersion, MediaReconciliationSources sources,
        MediaReconciliationOperation operation, RecipeId? recipe, Sha256 capabilities, Sha256 limits, RomDecoderIdentity decoder,
        string? kindId, string? encoderBuild, RomIsoDiscKind? discKind)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(decoder);
        if (jobId == Guid.Empty || requestedVersion <= 0 ||
            kindId is not null && (kindId.Length is < 1 or > 64 || !kindId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) ||
            encoderBuild is not null && (string.IsNullOrWhiteSpace(encoderBuild) || encoderBuild.Length > 256 || encoderBuild.Any(char.IsControl)) ||
            operation == MediaReconciliationOperation.Encode && (kindId is null || encoderBuild is null) || discKind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentException("Capture requires an exact active demand, operation, decoder and actual encoding context.");
        var witnesses = sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Where(input => input.CatalogWitness is not null).ToArray();
        if ((operation == MediaReconciliationOperation.Adopt) != (witnesses.Length == 1) || witnesses.Length > 1 ||
            operation == MediaReconciliationOperation.Adopt && (sources.Layout is not MediaReconciliationLayout.Original original ||
                decoder is not RomDecoderIdentity.Native || kindId is null || encoderBuild is not null || discKind is not null ||
                sources.Inputs.OfType<MediaReconciliationInput.Stored>().Count(input => input.Role == MediaReconciliationSourceRole.Cue) != 1 ||
                !sources.Inputs.OfType<MediaReconciliationInput.Logical>().Any() ||
                witnesses[0].CatalogWitness!.PlatformId != original.PlatformId || witnesses[0].CatalogWitness!.ReleaseId != original.ReleaseId ||
                witnesses[0].CatalogWitness!.CatalogFingerprint != original.CatalogFingerprint))
            throw new ArgumentException("Only canonical adoption captures one exact catalog-witnessed artifact under its genuine original scope, without an encoder.");
        JobId = jobId;
        RequestedVersion = requestedVersion;
        Sources = sources;
        Decoder = decoder;
        // Failure identity cannot accidentally omit a changed resident decoder or actual encoding context.
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("media-reconciliation-capabilities/v1");
            writer.Write(capabilities.AsSpan());
            writer.Write(decoder is RomDecoderIdentity.Native);
            if (decoder is RomDecoderIdentity.Native native)
            { writer.Write(native.Build.Rid); writer.Write(native.Build.UpstreamCommit.AsSpan()); writer.Write(native.Build.RecipeVersion); }
            else
                writer.Write(((RomDecoderIdentity.Managed)decoder).Build);
            writer.Write(kindId is not null);
            if (kindId is not null)
                writer.Write(kindId);
            writer.Write(encoderBuild is not null);
            if (encoderBuild is not null)
                writer.Write(encoderBuild);
            writer.Write(discKind is not null);
            if (discKind is { } observedKind)
                writer.Write((int)observedKind);
        }
        if (capabilities.IsEmpty)
            throw new ArgumentException("A captured host capability fingerprint is required.");
        var capturedCapabilities = Sha256.FromSpan(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        Identity = new(operation, recipe, sources.Fingerprint, capturedCapabilities, limits);
        KindId = kindId;
        EncoderBuild = encoderBuild;
        DiscKind = discKind;
    }
    public Guid JobId { get; }
    public long RequestedVersion { get; }
    public MediaReconciliationSources Sources { get; }
    public MediaReconciliationAttemptIdentity Identity { get; }
    public RomDecoderIdentity Decoder { get; }
    public string? KindId { get; }
    public string? EncoderBuild { get; }
    public RomIsoDiscKind? DiscKind { get; }
}

public sealed record MediaReconciliationDemandState(MediaReconciliationSubject Subject, long Version, bool ConversionRequested, bool CancelRequested);
public sealed record MediaReconciliationPriorFailure(Guid JobId, int AttemptNumber, string Code);
public sealed record MediaReconciliationAttempt(int Number, MediaReconciliationCapture Capture, MediaReconciliationPriorFailure? PriorFailure);
public sealed record MediaReconciliationJournalOutcome(Guid JobId, MediaReconciliationOutcomeKind Kind, string Code, long AcknowledgedVersion, DateTimeOffset CompletedAt);

/// <summary>Fenced durable execution protocol; media work runs outside these transactions.</summary>
public enum MediaReconciliationAttemptDisposition { Recorded, Failed, Superseded, Cancelled }
public sealed record MediaReconciliationAttemptOutcome(Guid JobId, int Number, Sha256 Fingerprint, MediaReconciliationAttemptDisposition Disposition,
    string Code, int? RepresentationId, Guid? VerificationId, Guid? CreationId, Guid? InspectionId, DateTimeOffset CompletedAt);

public interface IMediaReconciliationExecution
{
    Task<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken ct = default);
    /// <summary>Independent exact-attempt journal, including after a publication with an ambiguous commit result.</summary>
    Task<MediaReconciliationAttemptOutcome?> ReadAttemptOutcomeAsync(Guid jobId, int number, CancellationToken ct = default);
    Task<MediaReconciliationJournalOutcome?> ReadOutcomeAsync(Guid jobId, CancellationToken ct = default);
    Task<MediaReconciliationDemandState?> ReadDemandAsync(Guid jobId, CancellationToken ct = default);
    /// <summary>Stop obsolete captured work without acknowledging demand or inventing a measured result.</summary>
    Task<ErrorOr<Success>> SupersedeAttemptAsync(Guid jobId, string code, bool processTreeStopped, CancellationToken ct = default);
    Task<ErrorOr<MediaReconciliationAttempt>> CaptureAsync(MediaReconciliationCapture capture, CancellationToken ct = default);
    /// <summary>Append complete independent measurements and raw observations, then atomically journal the stopped attempt and release its pins.</summary>
    Task<ErrorOr<Success>> RecordVerificationAsync(MediaReconciliationAttempt attempt, RomMediaVerificationMeasurement measured,
        bool processTreeStopped, CancellationToken ct = default);
    /// <summary>Record a stopped attempt failure without settling newer demand. Only exact deterministic attempts may populate the failure cache.</summary>
    Task<ErrorOr<Success>> FailAttemptAsync(MediaReconciliationAttempt attempt, string code, bool deterministic,
        bool processTreeStopped, CancellationToken ct = default);
    Task<ErrorOr<MediaReconciliationJournalOutcome>> FinishAsync(Guid jobId, long requestedVersion, MediaReconciliationOutcomeKind kind,
        string code, bool processTreeStopped, CancellationToken ct = default);
}

public static class MediaReconciliationErrors
{
    public const string InvalidRequestCode = "MediaReconciliation.InvalidRequest";
    public static Error ActorRequired() => Error.Unauthorized("MediaReconciliation.ActorRequired", "A valid authenticated account is required.");
    public static Error InvalidRequest(string message) => Error.Validation(InvalidRequestCode, message);
    public static Error Changed => Error.Conflict("MediaReconciliation.Changed", "The current demand or exact source facts changed; replan before media work.");
    public static Error Custody => Error.Conflict("MediaReconciliation.Custody", "Stopped media custody is required before replacing an attempt or releasing its sources.");
    public static Error Measurement => Error.Validation("MediaReconciliation.Measurement", "The result does not match the complete captured source, operation and decoder.");
}
