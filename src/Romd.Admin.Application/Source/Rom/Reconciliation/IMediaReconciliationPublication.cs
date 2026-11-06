using ErrorOr;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>Owns current-policy recheck, complete fact publication, genuine provenance and ownership changes and stopped attempt journal in one transaction.</summary>
public interface IMediaReconciliationPublication
{
    Task<ErrorOr<Success>> PublishAsync(MediaReconciliationAttempt attempt, MediaReconciliationEncoded encoded, bool processTreeStopped, CancellationToken ct = default);
    Task<ErrorOr<Success>> AdoptAsync(MediaReconciliationAttempt attempt, MediaReconciliationAdopted adopted, bool processTreeStopped, CancellationToken ct = default);
    Task<ErrorOr<Success>> ReleaseAsync(MediaReconciliationAttempt attempt, int representationId, Guid verificationId, bool processTreeStopped, CancellationToken ct = default);
}
