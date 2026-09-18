using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Resolves <see cref="JobStorage" /> lazily and optionally: the readiness checks must work in
///     hosts where the Hangfire client is not registered (the "Testing" environment) and must not
///     force storage construction at composition time.
/// </summary>
public sealed class HangfireJobStorageProbe(IServiceProvider serviceProvider) : IHangfireStorageProbe
{
    public bool IsAvailable => ResolveStorage() is not null;

    public void TouchStorage()
    {
        var storage = ResolveStorage()
            ?? throw new InvalidOperationException("Hangfire storage is not registered in this host.");
        using var connection = storage.GetConnection();
    }

    public DateTimeOffset? GetNewestServerHeartbeatUtc()
    {
        var storage = ResolveStorage();
        if (storage is null)
        {
            return null;
        }

        DateTimeOffset? newest = null;
        foreach (var server in storage.GetMonitoringApi().Servers())
        {
            if (server.Heartbeat is not { } heartbeat)
            {
                continue;
            }

            var heartbeatUtc = new DateTimeOffset(DateTime.SpecifyKind(heartbeat, DateTimeKind.Utc));
            if (newest is null || heartbeatUtc > newest)
            {
                newest = heartbeatUtc;
            }
        }

        return newest;
    }

    private JobStorage? ResolveStorage() => serviceProvider.GetService<JobStorage>();
}
