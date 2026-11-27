using System.ComponentModel.DataAnnotations;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.PlayLater.Commands.AddPlayLaterTitle;
using Romd.Consumer.Application.PlayLater.Commands.RemovePlayLaterTitle;
using Romd.Consumer.Application.PlayLater.Queries.GetPlayLaterMembership;
using Romd.Consumer.Application.PlayLater.Queries.ListPlayLaterTitles;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Consumer.PlayLater;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerPlayLaterEndpoints
{
    public static IEndpointRouteBuilder MapConsumerPlayLaterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("me/play-later")
            .WithTags("Consumer Play Later")
            .RequireAuthorization(AuthorizationPolicies.RequireUser)
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                return await next(context);
            });

        group.MapGet("", ListTitles)
            .WithName("ListConsumerPlayLaterTitles")
            .WithDescription("List the caller's saved titles in the current published library, newest added first.")
            .Produces<Page<ConsumerTitleCard>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest);

        group.MapGet("membership", GetMembership)
            .WithName("GetConsumerPlayLaterMembership")
            .WithDescription("Return the requested title IDs that are saved by the caller and visible in the current published library.")
            .Produces<ConsumerPlayLaterMembership>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest);

        group.MapPut("{titleId}", AddTitle)
            .WithName("ReplaceConsumerPlayLaterTitle")
            .WithDescription("Save a visible title for the caller. Repeated saves preserve its original position.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        group.MapDelete("{titleId}", RemoveTitle)
            .WithName("DeleteConsumerPlayLaterTitle")
            .WithDescription("Remove a currently visible title from the caller's list. Unavailable titles and absent entries are left unchanged.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> ListTitles(
        string? cursor,
        IQueryHandler<ListPlayLaterTitlesQuery, Page<ConsumerTitleCard>> handler,
        CancellationToken ct,
        [Range(1, ListPlayLaterTitlesQuery.MaxLimit)] int limit = 50) =>
        (await handler.HandleAsync(new(cursor, limit), ct)).Match(value => Results.Ok(value), errors => errors.ToProblem());

    private static async Task<IResult> GetMembership(
        [FromQuery, MinLength(1), MaxLength(GetPlayLaterMembershipQuery.MaxTitleIds)] Sqid[] titleIds,
        IQueryHandler<GetPlayLaterMembershipQuery, ConsumerPlayLaterMembership> handler,
        CancellationToken ct) =>
        (await handler.HandleAsync(new(titleIds), ct)).Match(value => Results.Ok(value), errors => errors.ToProblem());

    private static async Task<IResult> AddTitle(
        Sqid titleId, ICommandHandler<AddPlayLaterTitleCommand, Success> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(titleId), ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem());

    private static async Task<IResult> RemoveTitle(
        Sqid titleId, ICommandHandler<RemovePlayLaterTitleCommand, Deleted> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(titleId), ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem());
}
