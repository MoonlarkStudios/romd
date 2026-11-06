using ErrorOr;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

public enum MediaReconciliationCause { Admin, Import, Facts, Readiness, Selection, Retry }

/// <summary>Accepted intent is independent of decoder, policy, source capture and the selection that originally created work.</summary>
public sealed record MediaReconciliationDemand
{
    public MediaReconciliationDemand(Guid requestId, MediaReconciliationSubject subject, bool conversionRequested,
        MediaReconciliationCause cause, Guid? actorId, Guid? selectionJobId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (requestId == Guid.Empty || !Enum.IsDefined(cause) || actorId == Guid.Empty || selectionJobId == Guid.Empty ||
            cause is MediaReconciliationCause.Admin or MediaReconciliationCause.Retry && actorId is null ||
            (cause == MediaReconciliationCause.Selection) != (selectionJobId is not null) ||
            subject is MediaReconciliationSubject.Representation && conversionRequested)
            throw new ArgumentException("Demand requires an exact request identity, subject, cause, actor and selection attribution.");
        RequestId = requestId;
        Subject = subject;
        ConversionRequested = conversionRequested;
        Cause = cause;
        ActorId = actorId;
        SelectionJobId = selectionJobId;
    }
    public Guid RequestId { get; }
    public MediaReconciliationSubject Subject { get; }
    public bool ConversionRequested { get; }
    public MediaReconciliationCause Cause { get; }
    public Guid? ActorId { get; }
    public Guid? SelectionJobId { get; }
}

public sealed record MediaReconciliationAcceptance(MediaReconciliationJob Job, bool Created, bool OwnedBySelection);

public interface IMediaReconciliationRequests
{
    /// <summary>Stage request, unique active subject demand and dispatch in the caller's ReadCommitted transaction.
    /// Multiple subjects must be accepted in subject order. Never flush unrelated tracked work or execute media here.</summary>
    Task<ErrorOr<MediaReconciliationAcceptance>> StageSubjectAsync(MediaReconciliationDemand demand, CancellationToken ct = default);

    /// <summary>Record stop intent only. The worker retains pins until independently stopped settlement.</summary>
    Task<ErrorOr<Success>> StageCancellationAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Retry terminal work with exact request attribution. Selections retain their original finite bounds.</summary>
    Task<ErrorOr<MediaReconciliationAcceptance>> StageRetryAsync(Guid requestId, Guid previousJobId, Guid actorId, CancellationToken ct = default);

    /// <summary>Capture finite high-watermarks and stage durable selection. Request IDs are exact idempotency identities.</summary>
    Task<ErrorOr<MediaReconciliationAcceptance>> StageSelectionAsync(Guid requestId, MediaReconciliationFilter filter,
        long limit, bool conversionRequested, MediaReconciliationCause cause, Guid? actorId, CancellationToken ct = default);
}
