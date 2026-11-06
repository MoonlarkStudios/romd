using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>Durable user-cancellation state of one job, as its family records it.</summary>
public enum JobCancellationSignal
{
    /// <summary>
    ///     The family keeps no durable cancellation request. The heartbeat never stops the attempt,
    ///     and an executor cancellation that is neither host shutdown nor lease loss terminates the job.
    /// </summary>
    Untracked,

    /// <summary>
    ///     The family tracks cancellation and none is recorded. An executor cancellation is
    ///     unattributed: the runner rethrows it so the intent is redelivered unchanged.
    /// </summary>
    NotRequested,

    /// <summary>A durable user cancellation is recorded and the owned work must be reconciled.</summary>
    Requested
}

/// <summary>
///     Heartbeat and cancellation-attribution phase of the job runner. The runner reads the signal on
///     every heartbeat (a <see cref="JobCancellationSignal.Requested" /> signal stops the attempt) and
///     again when the executor surfaces an <see cref="OperationCanceledException" /> that is not host
///     shutdown or execution-lease loss.
/// </summary>
public interface IJobCancellationPolicy<in TJob> where TJob : Job
{
    Task<JobCancellationSignal> ReadSignalAsync(Guid jobId, CancellationToken ct = default);
}
