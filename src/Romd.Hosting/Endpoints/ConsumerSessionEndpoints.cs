using System.ComponentModel.DataAnnotations;
using ErrorOr;
using OpenIddict.Abstractions;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account.Commands.RevokeConsumerSession;
using Romd.Consumer.Application.Account.Queries.ListConsumerSessions;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Account;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerSessionEndpoints
{
    public static void MapConsumerSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("me/sessions").WithTags("Consumer Account")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", List).WithName("ListConsumerSignInSessions")
            .Produces<Page<ConsumerSignInSession>>().ProducesProblems(400);
        group.MapDelete("{sessionId:guid}", Revoke).WithName("RevokeConsumerSignInSession")
            .Produces(204).ProducesProblems(404);
    }

    private static async Task<IResult> List(HttpContext context,
        IQueryHandler<ListConsumerSessionsQuery, Page<ConsumerSignInSession>> handler,
        string? cursor = null, [Range(1, 100)] int limit = 50, CancellationToken ct = default)
    {
        Guid? sessionId = Guid.TryParse(context.User.GetClaim(SessionClaims.Id), out var id) ? id : null;
        var result = await handler.HandleAsync(new(sessionId, cursor, limit), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> Revoke(Guid sessionId, ICommandHandler<RevokeConsumerSessionCommand, Success> handler,
        CancellationToken ct) =>
        (await handler.HandleAsync(new(sessionId), ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem());
}
