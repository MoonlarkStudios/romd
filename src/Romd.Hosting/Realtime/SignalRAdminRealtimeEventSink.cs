using Romd.Hosting.Dashboard;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Management.Realtime;
using Romd.Host.Hubs;
using Romd.Infrastructure.Realtime;

namespace Romd.Hosting.Realtime;

public sealed class SignalRAdminRealtimeEventSink(
    IReferenceCatalogService referenceCatalog,
    SignalRJobNotifier jobNotifier,
    SignalRStatsNotifier statsNotifier,
    DashboardStatsCache cache) : IAdminRealtimeEventSink
{
    public Task SendJobUpdatedAsync(JobDto job, int schemaVersion, CancellationToken ct = default) =>
        jobNotifier.SendJobUpdatedAsync(job, schemaVersion, ct);

    public async Task SendTitleEnrichedAsync(
        AdminRealtimeTitleEnrichedOutboxPayload payload,
        int schemaVersion,
        CancellationToken ct = default) =>
        await jobNotifier.SendTitleEnrichedAsync(
            new AdminRealtimeTitleEnrichedPayload(
                IdCoder.Encode(payload.TitleId),
                (await referenceCatalog.GetSystemKeysAsync(ct)).Required(payload.PlatformId),
                payload.CoverMediaId.HasValue ? IdCoder.Encode(payload.CoverMediaId.Value) : null),
            schemaVersion,
            ct);

    public Task SendLibraryUpdatedAsync(
        AdminRealtimeLibraryUpdatedPayload payload,
        int schemaVersion,
        CancellationToken ct = default) =>
        statsNotifier.SendLibraryUpdatedAsync(payload, schemaVersion, ct);

    public Task SendStorageChangedAsync(int schemaVersion, CancellationToken ct = default)
    {
        cache.InvalidateStorage();
        return statsNotifier.SendStorageChangedAsync(schemaVersion, ct);
    }

    public Task SendCoverageChangedAsync(int schemaVersion, CancellationToken ct = default)
    {
        cache.InvalidateCoverage();
        return statsNotifier.SendCoverageChangedAsync(schemaVersion, ct);
    }

    public Task SendHealthChangedAsync(int schemaVersion, CancellationToken ct = default)
    {
        cache.InvalidateHealth();
        return statsNotifier.SendHealthChangedAsync(schemaVersion, ct);
    }
}
