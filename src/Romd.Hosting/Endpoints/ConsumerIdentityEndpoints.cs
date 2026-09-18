using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Consumer.Application.Auth.Queries.GetCurrentConsumerUser;
using Romd.Consumer.Application.Libraries.Queries.GetCurrentLibraryContext;
using Romd.Contracts.Consumer.Auth;
using Romd.Contracts.Consumer.Libraries;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerIdentityEndpoints
{
    public static IEndpointRouteBuilder MapConsumerIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("")
            .WithTags("Consumer Identity")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("me", GetCurrentUser)
            .WithName("GetConsumerCurrentUser")
            .Produces<CurrentUserDto>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("me/library", GetCurrentLibrary)
            .WithName("GetConsumerCurrentLibrary")
            .Produces<LibraryContextDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetCurrentUser(
        IQueryHandler<GetCurrentConsumerUserQuery, CurrentUserDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetCurrentConsumerUserQuery(), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetCurrentLibrary(
        IQueryHandler<GetCurrentLibraryContextQuery, LibraryContextDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetCurrentLibraryContextQuery(), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }
}
