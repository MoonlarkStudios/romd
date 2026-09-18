namespace Romd.Domain.Jobs;

/// <summary>
///     Abstract base class for all job types.
///     Provides common properties and behavior for job tracking.
/// </summary>
public abstract class Job
{
    protected readonly List<JobError> _errors = [];

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
    public IReadOnlyList<JobError> Errors => _errors;

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
    public void SetCurrentItem(string? item) => CurrentItem = item;

    /// <summary>
    ///     Starts the job, recording the Hangfire job ID and transitioning from Pending.
    /// </summary>
    public void Start(string hangfireJobId)
    {
        HangfireJobId = hangfireJobId;
        StartedAt = DateTimeOffset.UtcNow;
        TransitionFromPending();
    }

    /// <summary>
    ///     Completes the job successfully.
    /// </summary>
    public void Complete()
    {
        ValidateCanComplete();
        SetTerminalPhase(HasErrors);
        CompletedAt = DateTimeOffset.UtcNow;
        CurrentItem = null;
    }

    /// <summary>
    ///     Fails the job with a reason.
    /// </summary>
    public void Fail(string reason)
    {
        if (IsTerminal) return;
        SetFailedPhase();
        CompletedAt = DateTimeOffset.UtcNow;
        AddError("job", reason);
    }

    /// <summary>
    ///     Cancels the job.
    /// </summary>
    public void Cancel()
    {
        if (IsTerminal) return;
        SetCancelledPhase();
        CompletedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Records an error during processing.
    /// </summary>
    public void RecordError(string item, string message) => AddError(item, message);

    /// <summary>
    ///     Archives the job (soft delete).
    /// </summary>
    public void Archive()
    {
        if (!IsTerminal)
        {
            throw new InvalidOperationException("Cannot archive an active job");
        }

        IsArchived = true;
        ArchivedAt = DateTimeOffset.UtcNow;
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
    /// </summary>
    protected abstract void TransitionFromPending();

    /// <summary>
    ///     Sets the terminal phase (Completed or CompletedWithErrors).
    /// </summary>
    protected abstract void SetTerminalPhase(bool hasErrors);

    /// <summary>
    ///     Sets the Failed phase.
    /// </summary>
    protected abstract void SetFailedPhase();

    /// <summary>
    ///     Sets the Cancelled phase.
    /// </summary>
    protected abstract void SetCancelledPhase();

    /// <summary>
    ///     Validates that the job is in the correct phase to complete.
    ///     Override to enforce phase-specific completion rules.
    /// </summary>
    protected virtual void ValidateCanComplete() { }

    /// <summary>
    ///     Adds an error to the job's error collection.
    /// </summary>
    protected void AddError(string item, string message)
        => _errors.Add(new JobError(item, message, DateTimeOffset.UtcNow));

    /// <summary>
    ///     Adds a categorised error tied to a specific entity, so the failure can
    ///     later be retried in isolation.
    /// </summary>
    protected void AddError(string item, string message, int? entityId, JobErrorReason reason)
        => _errors.Add(new JobError(item, message, DateTimeOffset.UtcNow, entityId, reason));
}
