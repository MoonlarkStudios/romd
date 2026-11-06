using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Host.Authorization;

namespace Romd.Host.Hubs;

[Authorize(Policy = AuthorizationPolicies.RequireUser)]
public sealed class JobHub(IJobRepository jobs) : Hub
{
    // Non-streaming SignalR RPCs keep their wire arguments; cancellation comes from the connection.
    public Task SubscribeToJob(string jobId) => SubscribeToJobAsync(jobId, Context.ConnectionAborted);

    public Task SubscribeToJobFeed() => SubscribeToJobFeedAsync(Context.ConnectionAborted);

    public Task SubscribeToTitle(string titleId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"title:{titleId}", Context.ConnectionAborted);

    private async Task SubscribeToJobAsync(string jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId, out var id) || id == Guid.Empty)
            throw new HubException("Job unavailable.");
        var job = await jobs.GetByIdAsync(id, ct);
        if (job is null)
            throw new HubException("Job unavailable.");
        bool media = job is MediaReconciliationJob;
        if (media && !IsAdmin)
            throw new HubException("Job unavailable.");
        await Groups.AddToGroupAsync(Context.ConnectionId, media ? $"job:admin:{id}" : $"job:{id}", ct);
    }

    private async Task SubscribeToJobFeedAsync(CancellationToken ct)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "jobs:all", ct);
        if (IsAdmin)
            await Groups.AddToGroupAsync(Context.ConnectionId, "jobs:admin", ct);
    }

    private bool IsAdmin => Context.User?.Identity?.IsAuthenticated == true && Context.User.IsInRole(RomdRoleType.Admin.ToString());
}
