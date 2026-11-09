using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Access.Queries.GetConsumerReleaseAccess;
using Romd.Contracts.Consumer.Releases;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerAccessEndpoints
{
    public static IEndpointRouteBuilder MapConsumerAccessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("releases/{releaseId}/access", GetConsumerReleaseAccess)
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                context.HttpContext.Response.Headers.Pragma = "no-cache";
                return await next(context);
            })
            .WithTags("Consumer Access")
            .RequireAuthorization(AuthorizationPolicies.RequireUser)
            .WithName("GetConsumerReleaseAccess")
            .Produces<ConsumerReleaseAccess>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetConsumerReleaseAccess(
        Sqid releaseId,
        IQueryHandler<GetConsumerReleaseAccessQuery, ConsumerReleaseAccess> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new GetConsumerReleaseAccessQuery(releaseId),
            ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }
}
