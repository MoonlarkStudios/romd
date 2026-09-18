using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerReleaseManifest;
using Romd.Consumer.Application.Delivery.Queries.GetConsumerPlatformBios;
using Romd.Contracts.Consumer.Delivery;
using Romd.Contracts.Consumer.Releases;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapConsumerDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("releases/{releaseId}")
            .WithTags("Consumer Delivery")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("manifest", IssueReleaseManifest)
            .WithName("IssueConsumerReleaseManifest")
            .Produces<ConsumerReleaseManifestDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        app.MapGet("systems/{systemKey}/bios", GetPlatformBios)
            .WithTags("Consumer Delivery")
            .RequireAuthorization(AuthorizationPolicies.RequireUser)
            .WithName("GetConsumerPlatformBios")
            .Produces<ConsumerPlatformBiosDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetPlatformBios(
        string systemKey,
        IQueryHandler<GetConsumerPlatformBiosQuery, ConsumerPlatformBiosDto> handler,
        CancellationToken ct)
    {
        var query = new GetConsumerPlatformBiosQuery(systemKey);
        var result = await handler.HandleAsync(query, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> IssueReleaseManifest(
        Sqid releaseId,
        ICommandHandler<IssueConsumerReleaseManifestCommand, ConsumerReleaseManifestDto> handler,
        CancellationToken ct)
    {
        var command = new IssueConsumerReleaseManifestCommand(releaseId);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }
}
