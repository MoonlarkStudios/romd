using Microsoft.AspNetCore.SignalR;
using Romd.Contracts.Management.Realtime;

namespace Romd.Host.Hubs;

public sealed class SignalRStatsNotifier(IHubContext<SystemHub> hubContext)
{
    private const string Group = "stats:all";

    // The schema version rides as a trailing SignalR argument on every send:
    // JS clients ignore extra trailing arguments, so existing clients are
    // unaffected.
    public Task SendStorageChangedAsync(int schemaVersion, CancellationToken ct = default) =>
        hubContext.Clients.Group(Group).SendAsync("StorageStatsChanged", schemaVersion, ct);

    public Task SendLibraryUpdatedAsync(
        AdminRealtimeLibraryUpdatedPayload payload,
        int schemaVersion,
        CancellationToken ct = default) =>
        hubContext.Clients.Group(Group).SendAsync("LibraryUpdated", payload, schemaVersion, ct);

    public Task SendCoverageChangedAsync(int schemaVersion, CancellationToken ct = default) =>
        hubContext.Clients.Group(Group).SendAsync("CoverageStatsChanged", schemaVersion, ct);

    public Task SendHealthChangedAsync(int schemaVersion, CancellationToken ct = default) =>
        hubContext.Clients.Group(Group).SendAsync("HealthStatsChanged", schemaVersion, ct);
}
