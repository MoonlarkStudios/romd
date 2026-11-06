using Romd.Admin.Application.Dashboard;

namespace Romd.Infrastructure.Dashboard;

public sealed class NoOpStatsNotifier : IStatsNotifier
{
    public Task NotifyStorageChangedAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyCoverageChangedAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyHealthChangedAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
