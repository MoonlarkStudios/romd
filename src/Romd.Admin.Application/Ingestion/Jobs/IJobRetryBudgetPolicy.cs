using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Durable deferral budget for a job family whose executors request retries through
///     <see cref="JobContext.RequestDeferral" />. The runner records each deferral on the job,
///     resets the count when the executor reports durable progress, and converts an exhausted
///     budget into a permanent failure.
/// </summary>
/// <param name="Label">Family label used in the persisted exhaustion message.</param>
/// <param name="MaximumAge">Elapsed time since the first deferral after which an attempt is not started.</param>
/// <param name="MaximumDeferralsWithoutProgress">
///     Consecutive no-progress deferrals tolerated before a requested deferral becomes a permanent
///     failure; null when the family waits on work it does not own and must not be counted out.
/// </param>
public sealed record JobRetryBudget(string Label, TimeSpan MaximumAge, int? MaximumDeferralsWithoutProgress);

/// <summary>
///     Deferral-accounting phase of the job runner. Families without a budget leave deferral counters
///     untouched; a requested deferral is still durably handed off through the execution claim.
/// </summary>
public interface IJobRetryBudgetPolicy<in TJob> where TJob : Job
{
    JobRetryBudget? BudgetFor(TJob job);
}
