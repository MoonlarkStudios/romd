using Microsoft.AspNetCore.SignalR;
using Romd.Contracts.Management.Realtime;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Hubs;

public sealed class SignalRJobNotifier(IHubContext<JobHub> hubContext)
{
    public async Task SendJobUpdatedAsync(Models.JobDto dto, int schemaVersion, CancellationToken ct = default)
    {
        // The schema version rides as a trailing SignalR argument: JS clients
        // ignore extra trailing arguments, so existing clients are unaffected.
        await Task.WhenAll(
            hubContext.Clients.Group($"job:{dto.Id}").SendAsync("JobUpdated", dto, schemaVersion, ct),
            hubContext.Clients.Group("jobs:all").SendAsync("JobUpdated", dto, schemaVersion, ct));
    }

    public async Task SendTitleEnrichedAsync(
        AdminRealtimeTitleEnrichedPayload payload,
        int schemaVersion,
        CancellationToken ct = default)
    {
        // Trailing schema version argument; JS clients ignore extra trailing args.
        await hubContext.Clients.Group($"title:{payload.TitleId}")
            .SendAsync("TitleEnriched", payload, schemaVersion, ct);
    }
}
