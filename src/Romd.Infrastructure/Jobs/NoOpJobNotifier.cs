using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs;

/// <summary>
///     No-op implementation of IJobNotifier used before SignalR is wired up
///     and during integration testing.
/// </summary>
public sealed class NoOpJobNotifier : IJobNotifier
{
    public Task NotifyJobUpdatedAsync(Job job, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyTitleEnrichedAsync(int titleId, int platformId, int? coverMediaId = null, CancellationToken ct = default)
        => Task.CompletedTask;
}
