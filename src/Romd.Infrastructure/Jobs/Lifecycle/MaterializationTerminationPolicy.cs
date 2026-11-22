using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Lifecycle;

public sealed class MaterializationTerminationPolicy(IMaterializationJobRepository jobs) : IJobTerminationPolicy<MaterializationJob>
{
    public async Task<bool> TryRecoverCommittedOutcomeAsync(MaterializationJob job, CancellationToken ct = default) =>
        await jobs.GetByIdAsync(job.Id, ct) is { } persisted && job.AdoptPublication(persisted);

    public async Task<JobTermination> ReconcileAsync(MaterializationJob job, JobTerminationCause cause, CancellationToken ct = default) =>
        await TryRecoverCommittedOutcomeAsync(job, ct) ? JobTermination.Settled : JobTermination.Transition;
}
