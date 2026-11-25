using Romd.Application.Common.Security;
using Romd.Application.Common.Cqrs;
using Romd.Consumer.Application.Auth.Queries.GetCurrentConsumerUser;
using Romd.Consumer.Application.Libraries.Queries.GetCurrentLibraryContext;
using Romd.Consumer.Application.Libraries.Queries.GetCurrentLibraryPublication;
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
            .WithMetadata(new ThirdPartyConsumerScope(ConsumerGrantScopes.Discovery))
            .Produces<CurrentUser>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("me/library", GetCurrentLibrary)
            .WithName("GetConsumerCurrentLibrary")
            .WithMetadata(new ThirdPartyConsumerScope(ConsumerGrantScopes.Library))
            .Produces<LibraryContext>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status404NotFound);

        group.MapGet("me/library/publication", async (
            IQueryHandler<GetCurrentLibraryPublicationQuery, LibraryPublicationVersion> handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new(), ct);
            return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
        }).WithName("GetConsumerLibraryPublication")
            .WithMetadata(new ThirdPartyConsumerScope(ConsumerGrantScopes.Library))
            .Produces<LibraryPublicationVersion>().Produces(401);

        return app;
    }

    private static async Task<IResult> GetCurrentUser(
        IQueryHandler<GetCurrentConsumerUserQuery, CurrentUser> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetCurrentConsumerUserQuery(), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetCurrentLibrary(
        IQueryHandler<GetCurrentLibraryContextQuery, LibraryContext> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetCurrentLibraryContextQuery(), cancellationToken);

        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }
}
