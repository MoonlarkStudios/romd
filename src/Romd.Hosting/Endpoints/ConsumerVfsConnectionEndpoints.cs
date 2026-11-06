using ErrorOr;
using System.ComponentModel.DataAnnotations;
using Romd.Domain.Vfs;
using Romd.Application.Common.Cqrs;
using Romd.Consumer.Application.Vfs;
using Romd.Consumer.Application.Vfs.Commands.CreateVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RenameVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RotateVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RevokeVfsConnection;
using Romd.Consumer.Application.Vfs.Queries.ListVfsConnections;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Common.Models;
using Romd.Host.Authorization;
using Romd.Hosting.Vfs;

namespace Romd.Host.Endpoints;

public static class ConsumerVfsConnectionEndpoints
{
    public static IEndpointRouteBuilder MapConsumerVfsConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("me/connections").WithTags("Consumer Connections")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });
        group.MapGet("", List).WithName("ListConsumerConnections").Produces<Page<VfsConnectionDto>>().ProducesProblems(400);
        group.MapPost("", Create).WithName("CreateConsumerConnection").ProducesCreated<VfsConnectionCredentialDto>()
            .ProducesProblems(400, 409, 503);
        group.MapPatch("{connectionId:guid}", Rename).WithName("UpdateConsumerConnection").Produces<VfsConnectionDto>()
            .ProducesProblems(400, 404);
        group.MapPost("{connectionId:guid}/rotate", Rotate).WithName("RotateConsumerConnection").Produces<VfsConnectionCredentialDto>()
            .ProducesProblems(404, 409, 503);
        group.MapDelete("{connectionId:guid}", Revoke).WithName("DeleteConsumerConnection").Produces(204).ProducesProblems(404);
        return app;
    }

    private static async Task<IResult> List(IQueryHandler<ListVfsConnectionsQuery, Page<VfsConnectionDto>> handler,
        string? cursor = null, [Range(1, ListVfsConnectionsQuery.MaxLimit)] int limit = 50, CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new ListVfsConnectionsQuery(cursor, limit), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> Create(SaveVfsConnectionRequest request,
        ICommandHandler<CreateVfsConnectionCommand, IssuedVfsCredential> handler, VfsConnectionExport export, CancellationToken ct)
    {
        if (export.Endpoint is not { } endpoint)
            return Unavailable();
        var format = VfsFormats.Parse(request.Format ?? "stored");
        if (format.IsError)
            return format.Errors.ToProblem();
        var result = await handler.HandleAsync(new CreateVfsConnectionCommand(request.Name, format.Value), ct);
        return result.IsError ? result.Errors.ToProblem()
            : Results.Created($"/api/me/connections/{result.Value.Connection.Id:D}", export.Build(result.Value, endpoint));
    }

    private static async Task<IResult> Rename(Guid connectionId, PatchVfsConnectionRequest request,
        ICommandHandler<RenameVfsConnectionCommand, VfsConnectionDto> handler, CancellationToken ct)
    {
        VfsFormat? format = null;
        if (request.Format is { } requested)
        {
            var parsed = VfsFormats.Parse(requested);
            if (parsed.IsError)
                return parsed.Errors.ToProblem();
            format = parsed.Value;
        }
        var result = await handler.HandleAsync(new RenameVfsConnectionCommand(connectionId, request.Name, format), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(result.Value);
    }

    private static async Task<IResult> Rotate(Guid connectionId,
        ICommandHandler<RotateVfsConnectionCommand, IssuedVfsCredential> handler, VfsConnectionExport export, CancellationToken ct)
    {
        if (export.Endpoint is not { } endpoint)
            return Unavailable();
        var result = await handler.HandleAsync(new RotateVfsConnectionCommand(connectionId), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.Ok(export.Build(result.Value, endpoint));
    }

    private static async Task<IResult> Revoke(Guid connectionId,
        ICommandHandler<RevokeVfsConnectionCommand, Success> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RevokeVfsConnectionCommand(connectionId), ct);
        return result.IsError ? result.Errors.ToProblem() : Results.NoContent();
    }

    private static IResult Unavailable() => ProblemResults.Problem(StatusCodes.Status503ServiceUnavailable,
        ProblemCodes.VfsConnectionsUnavailable, "VFS must be enabled with an HTTPS consumer public URL before issuing credentials.");
}
