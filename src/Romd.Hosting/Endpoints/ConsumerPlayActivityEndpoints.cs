using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Consumer.Application.Activity.Commands.ClearPlayActivity;
using Romd.Consumer.Application.Activity.Commands.DeletePlaySession;
using Romd.Consumer.Application.Activity.Commands.UpsertPlaySession;
using Romd.Consumer.Application.Activity.Queries.GetPlaySession;
using Romd.Consumer.Application.Activity.Queries.ListPlaySessions;
using Romd.Consumer.Application.Activity.Queries.ListRecentlyPlayed;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Activity;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ConsumerPlayActivityEndpoints
{
    public static IEndpointRouteBuilder MapConsumerPlayActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("me/activity")
            .WithTags("Consumer Play Activity")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPut("play-sessions/{sessionId:guid}", UpsertSession)
            .WithName("UpsertPlaySession")
            .Produces<PlaySessionDto>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapGet("play-sessions/{sessionId:guid}", GetSession)
            .WithName("GetPlaySession")
            .Produces<PlaySessionDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapGet("play-sessions", ListSessions)
            .WithName("ListPlaySessions")
            .Produces<Page<PlaySessionDto>>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapDelete("play-sessions/{sessionId:guid}", DeleteSession)
            .WithName("DeletePlaySession")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapDelete("play-sessions", ClearSessions)
            .WithName("ClearPlaySessions")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("recently-played", ListRecentlyPlayed)
            .WithName("ListRecentlyPlayed")
            .Produces<IReadOnlyList<RecentlyPlayedTitleDto>>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> UpsertSession(
        Guid sessionId,
        [FromBody] UpsertPlaySessionRequest request,
        ICommandHandler<UpsertPlaySessionCommand, PlaySessionDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new UpsertPlaySessionCommand(sessionId, request), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> GetSession(
        Guid sessionId,
        IQueryHandler<GetPlaySessionQuery, PlaySessionDto> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetPlaySessionQuery(sessionId), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> ListSessions(
        string? cursor,
        int? limit,
        IQueryHandler<ListPlaySessionsQuery, Page<PlaySessionDto>> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListPlaySessionsQuery(cursor, limit ?? 50), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> DeleteSession(
        Guid sessionId,
        ICommandHandler<DeletePlaySessionCommand, Deleted> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new DeletePlaySessionCommand(sessionId), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.NoContent();
    }

    private static async Task<IResult> ClearSessions(
        ICommandHandler<ClearPlayActivityCommand, Deleted> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ClearPlayActivityCommand(), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.NoContent();
    }

    private static async Task<IResult> ListRecentlyPlayed(
        int? limit,
        IQueryHandler<ListRecentlyPlayedQuery, IReadOnlyList<RecentlyPlayedTitleDto>> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListRecentlyPlayedQuery(limit ?? 18), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }
}
