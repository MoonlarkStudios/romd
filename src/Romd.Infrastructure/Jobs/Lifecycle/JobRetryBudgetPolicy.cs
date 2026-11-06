using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Lifecycle;

/// <summary>A fixed deferral budget registered for one job family.</summary>
public sealed class JobRetryBudgetPolicy<TJob> : IJobRetryBudgetPolicy<TJob>
    where TJob : Job
{
    private readonly JobRetryBudget _budget;
    public JobRetryBudgetPolicy(JobRetryBudget budget) => this._budget = budget;
    public JobRetryBudget? BudgetFor(TJob job) => _budget;
}
