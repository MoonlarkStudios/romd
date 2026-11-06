using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

public interface IJobNotifier
{
    Task NotifyJobUpdatedAsync(Job job, CancellationToken ct = default);

    Task NotifyTitleEnrichedAsync(int titleId, int platformId, int? coverMediaId = null, CancellationToken ct = default);
}
