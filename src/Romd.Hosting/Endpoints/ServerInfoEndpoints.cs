using Romd.Contracts.Management.Models;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ServerInfoEndpoints
{
    public static void MapServerInfoEndpoint(this IEndpointRouteBuilder api, ServerInfo info)
    {
        api.MapGet("/server/info", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(info);
            })
            .WithTags("Server")
            .WithName("GetServerInfo")
            .WithDescription("Returns the installed release and the running admin host's build.")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
    }
}
