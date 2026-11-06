using ErrorOr;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Fenced persistence operations inside the caller's transaction. Preparation locks and rechecks complete current authority before CAS ledger publication.</summary>
public interface IMediaReconciliationPublicationStore
{
    Task<ErrorOr<Success>> PrepareAsync(MediaReconciliationAttempt attempt, IReadOnlyCollection<Sha256> publicationHashes, CancellationToken ct = default);
    Task<ErrorOr<Success>> StageEncodedAsync(MediaReconciliationAttempt attempt, MediaReconciliationEncoded encoded, int artifactFileId,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct = default);
    Task<ErrorOr<Success>> StageAdoptedAsync(MediaReconciliationAttempt attempt, MediaReconciliationAdopted adopted, int artifactFileId,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct = default);
    Task<ErrorOr<Success>> StageReleaseAsync(MediaReconciliationAttempt attempt, int representationId, Guid verificationId, CancellationToken ct = default);
}
public static class MediaReconciliationPublicationErrors
{
    public static Error LockScopeChanged => Error.Conflict("MediaReconciliation.LockScopeChanged", "Concurrent ownership changed the complete publication lock set; restart the transaction with the same measured payload.");
}
