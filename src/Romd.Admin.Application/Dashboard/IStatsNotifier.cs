namespace Romd.Admin.Application.Dashboard;

public interface IStatsNotifier
{
    Task NotifyStorageChangedAsync(CancellationToken ct = default);
    Task NotifyCoverageChangedAsync(CancellationToken ct = default);
    Task NotifyHealthChangedAsync(CancellationToken ct = default);
}
