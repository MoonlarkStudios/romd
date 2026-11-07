using ErrorOr;
using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.ReferenceData.Systems.Commands;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Host.Authorization;

using ContractSystemOverrides = Romd.Contracts.Common.ReferenceCatalog.SystemOverrides;

namespace Romd.Host.Endpoints;

public static class SystemReferenceEndpoints
{
    public static void MapSystemReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("systems").WithTags("systems").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] ISystemReferenceReader reader, string? query = null, string? cursor = null,
            [Range(1, ReferencePageRequest.MaxLimit)] int limit = ReferencePageRequest.DefaultLimit, string? sortBy = null, CancellationToken ct = default) =>
        {
            var request = ReferencePageRequest.Create(prefix + ":systems", query, cursor, limit, sortBy);
            return request.IsError ? request.Errors.ToProblem() : Results.Ok(await reader.ListAsync(request.Value, ct));
        }).WithName("List" + prefix + "Systems")
            .WithDescription("Registered systems in ascending PostgreSQL C (UTF-8 byte) natural-key order. Bounded keyset traversal; query matches normalized display facts. Default/max limit 100; sortBy supports key:asc. Each page is one coherent observation, not a complete-traversal snapshot.")
            .Produces<Page<SystemResource>>();
        group.MapGet("{systemKey}", async (SystemKeyParameter systemKey, [FromServices] ISystemReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(systemKey.Key, ct) is { } system
                ? Preconditions.ConditionalRead(context, system.Resource, system.Revision)
                : ReferenceCatalogEndpoints.UnknownSystem())
            .WithName("Get" + prefix + "System").Produces<SystemResource>().ConditionalRead();
        if (!admin)
            return;
        group.MapPost("", Create).WithName("CreateSystem").RequireAuthorization(AuthorizationPolicies.RequireManager)
            .ProducesCreated<SystemResource>().ProducesProblems(400, 409).ConditionalCreate();
        // The system-key seam declares the unknown-key 404 of every {systemKey} route.
        group.MapPatch("{systemKey}", Update).WithName("UpdateSystem").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<SystemResource>().ProducesProblems(400, 409).ConditionalWrite();
        group.MapGet("{systemKey}/overrides", async (SystemKeyParameter systemKey, [FromServices] ISystemReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(systemKey.Key, ct) is { } system
                ? Preconditions.ConditionalRead(context, system.Overrides, system.Revision)
                : ReferenceCatalogEndpoints.UnknownSystem())
            .WithName("GetSystemOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<ContractSystemOverrides>()
            .ConditionalRead();
        group.MapDelete("{systemKey}/overrides", Reset).WithName("DeleteSystemOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).ProducesProblems(409).ConditionalWrite();
        group.MapDelete("{systemKey}/overrides/{field}", ResetField).WithName("DeleteSystemOverride").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).ProducesProblems(400, 409).ConditionalWrite();
        group.MapDelete("{systemKey}", Delete).WithName("DeleteSystem").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).ProducesProblems(409).ConditionalWriteWithoutETag();
    }
    private static async Task<IResult> Create(CreateSystem request, [FromServices] ICommandHandler<CreateSystemCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>> handler, HttpContext context, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.Key, new(request.Name, request.CompactLabel, request.Description, request.Icon, request.Monochrome), request.ManufacturerKeys), ct);
        return result.Match(
            created => Results.Created("/api/systems/" + Uri.EscapeDataString(created.Resource.Key), created.Resource).WithETag(context, created.Revision),
            errors => errors.ToProblem());
    }
    private static async Task<IResult> Update(SystemKeyParameter systemKey, SystemPatch patch, HttpContext context,
        [FromServices] ICommandHandler<UpdateSystemCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>> handler, CancellationToken ct)
    {
        if (patch.Changes.ContainsKey("manufacturerKeys") && patch.ManufacturerKeys is null)
            return ProblemResults.Problem(400, "ReferenceResource.Invalid", "Manufacturer keys cannot be null.");
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(systemKey.Key, new(
            patch.Name,
            patch.CompactLabel,
            patch.Description,
            patch.Icon,
            patch.Monochrome, patch.Changes.ContainsKey("description"), patch.Changes.ContainsKey("icon")
        ), ifMatch.Value, patch.ManufacturerKeys), ct);
        return result.Match(updated => Results.Ok(updated.Resource).WithETag(context, updated.Revision), errors => errors.ToProblem());
    }
    private static async Task<IResult> Reset(SystemKeyParameter systemKey, HttpContext context,
        [FromServices] ICommandHandler<ResetSystemOverridesCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>> handler, CancellationToken ct)
    {
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(systemKey.Key, null, ifMatch.Value), ct);
        return result.Match(reset => Results.NoContent().WithETag(context, reset.Revision), errors => errors.ToProblem());
    }
    private static async Task<IResult> ResetField(SystemKeyParameter systemKey, string field, HttpContext context,
        [FromServices] ICommandHandler<ResetSystemOverridesCommand, ReferenceResourceResult<SystemResource, ContractSystemOverrides>> handler, CancellationToken ct)
    {
        if (!Enum.GetNames<SystemOverrideField>().Contains(field, StringComparer.OrdinalIgnoreCase))
            return ProblemResults.Problem(400, "ReferenceResource.Invalid", "Unknown override field.");
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(systemKey.Key, Enum.Parse<SystemOverrideField>(field, true), ifMatch.Value), ct);
        return result.Match(reset => Results.NoContent().WithETag(context, reset.Revision), errors => errors.ToProblem());
    }
    private static async Task<IResult> Delete(SystemKeyParameter systemKey, HttpContext context,
        [FromServices] ICommandHandler<DeleteSystemCommand, Deleted> handler, CancellationToken ct)
    {
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(systemKey.Key, ifMatch.Value), ct);
        return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
    }
}
