using Romd.Admin.Application.Titles.Enrichment;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Jobs;

public sealed class EnrichmentJobEnqueuer(JobDispatchService dispatcher) : IEnrichmentJobEnqueuer
{
    public Task EnqueueAsync(Guid jobId, CancellationToken ct = default) =>
        dispatcher.DispatchAsync(jobId, ct);
}
