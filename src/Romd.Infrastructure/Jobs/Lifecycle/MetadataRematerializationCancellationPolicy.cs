using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
namespace Romd.Infrastructure.Jobs.Lifecycle;

/// <summary>Unattributed cancellation preserves required metadata work for redelivery.</summary>
public sealed class MetadataRematerializationCancellationPolicy : IJobCancellationPolicy<MetadataRematerializationJob>
{
    public Task<JobCancellationSignal> ReadSignalAsync(Guid jobId, CancellationToken ct = default) =>
        Task.FromResult(JobCancellationSignal.NotRequested);
}
