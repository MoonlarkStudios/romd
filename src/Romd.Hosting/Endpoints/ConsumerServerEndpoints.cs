using Romd.Application.Common.Configuration;
using Romd.Contracts.Consumer.Server;

namespace Romd.Host.Endpoints;

public static class ConsumerServerEndpoints
{
    public static IEndpointRouteBuilder MapConsumerServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("server/identity", GetIdentity)
            .WithTags("Consumer Server")
            .WithName("GetConsumerServerIdentity")
            .AllowAnonymous()
            .Produces<ConsumerServerIdentityDto>();

        return app;
    }

    private static IResult GetIdentity(IServerInstanceIdentity identity) =>
        Results.Ok(new ConsumerServerIdentityDto
        {
            InstanceId = identity.InstanceId.ToString("D")
        });
}
