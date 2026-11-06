using Romd.Host.Hubs;

namespace Romd.Hosting;

public static class RomdEndpointRouteExtensions
{
    public static IEndpointRouteBuilder MapRomdRealtimeHubs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<JobHub>("/hubs/jobs");
        endpoints.MapHub<SystemHub>("/hubs/system");

        return endpoints;
    }
}
