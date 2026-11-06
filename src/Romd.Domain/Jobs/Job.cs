using System.Collections.ObjectModel;

namespace Romd.Domain.Jobs;

/// <summary>
///     Abstract base class for all job types.
///     Provides common properties and behavior for job tracking.
/// </summary>
public abstract class Job
{
    protected readonly List<JobError> _errors = [];
    private readonly ReadOnlyCollection<JobError> _errorsView;

    protected Job()
    {
        _errorsView = _errors.AsReadOnly();
    }

    /// <summary>
    ///     Unique identifier for this job.
    /// </summary>
    public Guid Id { get; protected set; }

    /// <summary>
    ///     Correlation ID for distributed tracing.
    /// </summary>
    public Guid CorrelationId { get; protected set; }

    /// <summary>
    ///     Original filename of the source file.
    /// </summary>
    public string SourceFilename { get; protected set; } = null!;

    /// <summary>
    ///     Optional platform ID for scoping imports.
    /// </summary>
    public int? PlatformId { get; protected set; }

    /// <summary>
    ///     Hangfire job ID for cancellation support.
    /// </summary>
    public string? HangfireJobId { get; protected set; }

    /// <summary>
    ///     Current item being processed (for UI display).
    /// </summary>
    public string? CurrentItem { get; protected set; }

    /// <summary>
    ///     Errors that occurred during processing.
    /// </summary>
    public IReadOnlyList<JobError> Errors => _errorsView;

    /// <summary>
    ///     Whether any errors occurred.
    /// </summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>
    ///     When the job was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; protected set; }

    /// <summary>
    ///     When the job started processing.
    /// </summary>
    public DateTimeOffset? StartedAt { get; protected set; }

    /// <summary>
    ///     When the job completed (success, failure, or cancellation).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; protected set; }

    /// <summary>
    ///     When a deferred execution is next attempted (null when not deferred).
    ///     Only the persisted execution claim protocol schedules attempts.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; internal set; }
    /// <summary>Consecutive deferrals without a newly durable upload outcome.</summary>
    public int DeferralCount { get; internal set; }
    public string? LastDeferralError { get; internal set; }
    public DateTimeOffset? LastDeferralAt { get; internal set; }
    public DateTimeOffset? FirstDeferralAt { get; internal set; }

    public void RecordDeferral(string reason, DateTimeOffset at, bool madeProgress = false)
    {
        DeferralCount = madeProgress ? 0 : DeferralCount + 1;
        LastDeferralAt = at;
        FirstDeferralAt ??= LastDeferralAt;
        LastDeferralError = reason.Length <= 1000 ? reason : reason[..1000];
    }

    public void ResetDeferralBudget() => DeferralCount = 0;

    /// <summary>
    ///     Whether the job is archived (soft deleted).
    /// </summary>
    public bool IsArchived { get; protected set; }

    /// <summary>
    ///     When the job was archived.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; protected set; }

    /// <summary>
    ///     The ID of the user who created this job.
    ///     Captured at creation time for authorization in background jobs.
    /// </summary>
    public Guid? CreatedByUserId { get; protected set; }

    /// <summary>
    ///     Human-readable phase name for display.
    /// </summary>
    public abstract string Phase { get; }

    /// <summary>
    ///     Whether the job is in a terminal state.
    /// </summary>
    public abstract bool IsTerminal { get; }

    /// <summary>
    ///     Progress percentage (0-100).
    /// </summary>
    public abstract double ProgressPercent { get; }

    /// <summary>
    ///     Duration of the job (null if not started or not completed).
    /// </summary>
    public TimeSpan? Duration => StartedAt.HasValue && CompletedAt.HasValue
        ? CompletedAt.Value - StartedAt.Value
        : null;

    /// <summary>
    ///     Sets the Hangfire job ID.
    /// </summary>
    public void SetHangfireJobId(string id) => HangfireJobId = id;

    /// <summary>
    ///     Updates the current item being processed.
    /// </summary>
    public void SetCurrentItem(string? item) => CurrentItem = item is { Length: > 500 } ? item[..500] : item;

    /// <summary>
    ///     Starts the job, recording the Hangfire job ID and transitioning from Pending.
    /// </summary>
    public void Start(string hangfireJobId, DateTimeOffset startedAt)
    {
        TransitionFromPending();
        HangfireJobId = hangfireJobId;
        StartedAt = startedAt;
    }

    /// <summary>
    ///     Records an error during processing.
    /// </summary>
    public void RecordError(string item, string message, DateTimeOffset occurredAt) => AddError(item, message, occurredAt);

    /// <summary>
    ///     Archives the job (soft delete).
    /// </summary>
    public void Archive(DateTimeOffset archivedAt)
    {
        if (!IsTerminal)
        {
            throw new InvalidOperationException("Cannot archive an active job");
        }

        IsArchived = true;
        ArchivedAt = archivedAt;
    }

    /// <summary>
    ///     Unarchives the job.
    /// </summary>
    public void Unarchive()
    {
        IsArchived = false;
        ArchivedAt = null;
    }

    /// <summary>
    ///     Transitions the job from its Pending state to the first active phase.
    ///     Reject an invalid transition before changing any state.
    /// </summary>
    protected abstract void TransitionFromPending();

    /// <summary>
    ///     Adds an error to the job's error collection.
    /// </summary>
    protected void AddError(string item, string message, DateTimeOffset occurredAt)
        => _errors.Add(new JobError(item, message, occurredAt));

    /// <summary>
    ///     Adds a categorised error tied to a specific entity, so the failure can
    ///     later be retried in isolation.
    /// </summary>
    protected void AddError(string item, string message, int? entityId, JobErrorReason reason, DateTimeOffset occurredAt)
        => _errors.Add(new JobError(item, message, occurredAt, entityId, reason));
}
