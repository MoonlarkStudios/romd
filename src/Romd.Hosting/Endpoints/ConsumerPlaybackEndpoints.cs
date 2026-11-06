using Romd.Contracts.Consumer.Playback;
using Romd.Hosting;

namespace Romd.Host.Endpoints;

public static class ConsumerPlaybackEndpoints
{
    public static IEndpointRouteBuilder MapConsumerPlaybackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("playback/config", GetPlaybackConfig)
            .WithTags("Consumer Playback")
            .WithName("GetConsumerPlaybackConfig")
            .AllowAnonymous()
            .Produces<ConsumerPlaybackConfigDto>();

        return app;
    }

    private static IResult GetPlaybackConfig(HttpContext context, BrowserPlaybackOptions options)
    {
        context.Response.Headers.CacheControl = "no-store";
        string requestOrigin = $"{context.Request.Scheme}://{context.Request.Host.Value}";

        return Results.Ok(new ConsumerPlaybackConfigDto
        {
            PlayerOrigin = options.ResolvePlayerOrigin(requestOrigin)
        });
    }
}
