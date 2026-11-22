using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Lifecycle;

public sealed class UploadTerminationPolicy(IJobItemRepository items) : IJobTerminationPolicy<UploadJob>
{
    public Task<bool> TryRecoverCommittedOutcomeAsync(UploadJob job, CancellationToken ct = default) =>
        Task.FromResult(false);

    public async Task<JobTermination> ReconcileAsync(UploadJob job, JobTerminationCause cause, CancellationToken ct = default)
    {
        if (job.IsIngesting)
        {
            var outcomes = await items.GetOutcomesAsync(job.Id, ct);
            job.TryRestoreRecordedIngestionProgress(outcomes);
        }
        return JobTermination.Transition;
    }
}
