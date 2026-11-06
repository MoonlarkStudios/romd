using Hangfire;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Infrastructure.Jobs;

public sealed class HangfireJobDeliveryCancellation(IBackgroundJobClient jobs) : IJobDeliveryCancellation
{
    public Task RequestStopAsync(string deliveryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        jobs.Delete(deliveryId);
        return Task.CompletedTask;
    }
}
