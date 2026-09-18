using Microsoft.AspNetCore.Mvc;
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
        app.MapPost("releases/{releaseId}/access", GetConsumerReleaseAccess)
            .WithTags("Consumer Access")
            .RequireAuthorization(AuthorizationPolicies.RequireUser)
            .WithName("GetConsumerReleaseAccess")
            .Produces<ConsumerReleaseAccessDto>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetConsumerReleaseAccess(
        string releaseId,
        IQueryHandler<GetConsumerReleaseAccessQuery, ConsumerReleaseAccessDto> handler,
        CancellationToken ct)
    {
        if (!IdCoder.TryDecode(releaseId, out int decodedReleaseId) ||
            !string.Equals(IdCoder.Encode(decodedReleaseId), releaseId, StringComparison.Ordinal))
        {
            return Results.Problem(
                title: "Bad Request",
                detail: "The release identifier is invalid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await handler.HandleAsync(
            new GetConsumerReleaseAccessQuery(decodedReleaseId),
            ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }
}
