using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

public interface IJobExecutor<in TJob> where TJob : Job
{
    Task ExecuteAsync(TJob job, JobContext context);
}

/// <summary>
///     Opt-in capability for executors that dispatch on the job's persisted phase and can
///     safely resume after a crash or redelivery. The runner starts such jobs only from
///     Pending, keeps the workspace across a failed attempt, and rethrows executor failures
///     so the scheduler redelivers instead of terminally failing the job. Executors without
///     this capability keep fail-on-redelivery semantics.
/// </summary>
public interface IResumableJobExecutor<in TJob> : IJobExecutor<TJob> where TJob : Job;
