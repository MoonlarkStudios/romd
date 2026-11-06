using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>Why the runner is about to end a claimed attempt.</summary>
public enum JobTerminationCause
{
    /// <summary>A cancellation the cancellation policy attributed to the user (or does not track).</summary>
    Cancelled,

    /// <summary>A <see cref="JobPermanentFailureException" />, including an exhausted retry budget.</summary>
    PermanentFailure
}

public enum JobTerminationKind
{
    /// <summary>The runner applies its standard Cancel or Fail transition and checkpoints it under the fence.</summary>
    Transition,

    /// <summary>
    ///     A family journal durably committed the terminal outcome and the policy applied it to the job.
    ///     The runner publishes it without another checkpoint.
    /// </summary>
    Settled,

    /// <summary>
    ///     Family custody (child processes or owned child jobs) is unresolved. The runner records the
    ///     reason as a deferral and durably hands the claim off with backoff instead of terminating.
    /// </summary>
    Deferred
}

/// <summary>Family decision returned to the runner before it terminates a claimed attempt.</summary>
public sealed record JobTermination
{
    private JobTermination(JobTerminationKind kind, string? deferralReason)
    {
        Kind = kind;
        DeferralReason = deferralReason;
    }

    public JobTerminationKind Kind { get; }

    /// <summary>Persisted deferral reason; set only for <see cref="JobTerminationKind.Deferred" />.</summary>
    public string? DeferralReason { get; }

    public static JobTermination Transition { get; } = new(JobTerminationKind.Transition, null);

    public static JobTermination Settled { get; } = new(JobTerminationKind.Settled, null);

    public static JobTermination Defer(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(JobTerminationKind.Deferred, reason);
    }
}

/// <summary>
///     Recovery and termination phases of the job runner. Every call runs while the attempt's execution
///     fence is ambient and after the heartbeat has stopped; family stores must remain fenced so a lost
///     claim cannot publish terminal state.
/// </summary>
public interface IJobTerminationPolicy<in TJob> where TJob : Job
{
    /// <summary>
    ///     After the executor returns, before ownership is re-checked: apply an outcome that a family
    ///     journal already committed. Returns true when the job now mirrors that durable outcome, which
    ///     the runner then publishes without checkpointing, deferring, or completing the job.
    /// </summary>
    Task<bool> TryRecoverCommittedOutcomeAsync(TJob job, CancellationToken ct = default);

    /// <summary>
    ///     Reconcile family custody before the runner ends the attempt. Implementations honour a
    ///     committed journal and confirm process or child custody before allowing termination.
    /// </summary>
    Task<JobTermination> ReconcileAsync(TJob job, JobTerminationCause cause, CancellationToken ct = default);
}
