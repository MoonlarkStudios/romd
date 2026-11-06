using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Lifecycle;

/// <summary>Subjects have bounded no-progress retries; finite selections can wait on healthy child work.</summary>
public sealed class MediaReconciliationRetryBudgetPolicy : IJobRetryBudgetPolicy<MediaReconciliationJob>
{
    private readonly JobRetryBudget _subject;
    private readonly JobRetryBudget _selection;
    public MediaReconciliationRetryBudgetPolicy(TimeSpan maximumAge, int maximumSubjectDeferrals)
    {
        if (maximumAge <= TimeSpan.Zero || maximumSubjectDeferrals < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        _subject = new("Media reconciliation", maximumAge, maximumSubjectDeferrals);
        _selection = new("Media reconciliation", maximumAge, null);
    }
    public JobRetryBudget? BudgetFor(MediaReconciliationJob job) => job.Request is MediaReconciliationRequest.SelectionWork ? _selection : _subject;
}
