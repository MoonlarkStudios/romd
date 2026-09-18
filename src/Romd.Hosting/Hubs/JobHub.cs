using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Romd.Host.Authorization;

namespace Romd.Host.Hubs;

[Authorize(Policy = AuthorizationPolicies.RequireUser)]
public sealed class JobHub : Hub
{
    public Task SubscribeToJob(string jobId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");

    public Task SubscribeToJobFeed()
        => Groups.AddToGroupAsync(Context.ConnectionId, "jobs:all");

    public Task SubscribeToTitle(string titleId)
        => Groups.AddToGroupAsync(Context.ConnectionId, $"title:{titleId}");
}
