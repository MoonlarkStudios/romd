using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Romd.Host.Authorization;

namespace Romd.Host.Hubs;

[Authorize(Policy = AuthorizationPolicies.RequireUser)]
public sealed class SystemHub : Hub
{
    public Task SubscribeToStats()
        => Groups.AddToGroupAsync(Context.ConnectionId, "stats:all");
}
