using Romd.Admin.Application.Libraries;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Libraries;

public sealed class MaterializationJobEnqueuer(JobDispatchService dispatcher) : IMaterializationJobEnqueuer
{
    public Task EnqueueAsync(Guid jobId, CancellationToken ct = default) =>
        dispatcher.DispatchAsync(jobId, ct);
}
