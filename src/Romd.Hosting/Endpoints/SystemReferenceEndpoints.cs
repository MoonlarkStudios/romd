using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.ReferenceData.Systems.Commands;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class SystemReferenceEndpoints
{
    public static void MapSystemReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("systems").WithTags("systems").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] ISystemReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("List" + prefix + "Systems").Produces<IReadOnlyList<SystemResourceDto>>();
        group.MapGet("{key}", async (string key, [FromServices] ISystemReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(key, ct), false))
            .WithName("Get" + prefix + "Systems").Produces<SystemResourceDto>().Produces(304).Produces(404);
        if (!admin)
            return;
        group.MapPost("", Create).WithName("CreateSystems").RequireAuthorization(AuthorizationPolicies.RequireManager)
            .Produces<SystemResourceDto>(201).Produces(400).Produces(409);
        group.MapPatch("{key}", Update).WithName("PatchSystems").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<SystemResourceDto>().Produces(400).Produces(404).Produces(409).Produces(412).Produces(428);
        group.MapGet("{key}/overrides", async (string key, [FromServices] ISystemReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(key, ct), true))
            .WithName("GetSystemsOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<SystemOverridesDto>().Produces(304).Produces(404);
        group.MapDelete("{key}/overrides", Reset).WithName("ResetSystemsOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).Produces(404).Produces(409).Produces(412).Produces(428);
        group.MapDelete("{key}/overrides/{field}", ResetField).WithName("ResetSystemsOverride").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).Produces(400).Produces(404).Produces(409).Produces(412).Produces(428);
        group.MapDelete("{key}", Delete).WithName("DeleteSystems").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).Produces(404).Produces(409).Produces(412).Produces(428);
    }
    private static async Task<IResult> Create(CreateSystemDto request, [FromServices] ICommandHandler<CreateSystemCommand, ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>> handler, HttpContext context, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.Key, new(request.Name, request.CompactLabel, request.Description, request.Icon, request.Monochrome), request.ManufacturerKeys), ct);
        return ReferenceHttp.Edited(context, result, location: resource => "/api/systems/" + Uri.EscapeDataString(resource.Key));
    }
    private static async Task<IResult> Update(string key, SystemPatchDto patch, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<UpdateSystemCommand, ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>> handler, HttpContext context, CancellationToken ct)
    {
        if (patch.Changes.ContainsKey("manufacturerKeys") && patch.ManufacturerKeys is null)
            return ProblemResults.Problem(400, "ReferenceResource.Invalid", "Manufacturer keys cannot be null.");
        return ReferenceHttp.Edited(context, await handler.HandleAsync(new(key, new(
            patch.Name,
            patch.CompactLabel,
            patch.Description,
            patch.Icon,
            patch.Monochrome, patch.Changes.ContainsKey("description"), patch.Changes.ContainsKey("icon")
        ), ifMatch, patch.ManufacturerKeys), ct));
    }
    private static async Task<IResult> Reset(string key, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<ResetSystemOverridesCommand, ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>> handler, HttpContext context, CancellationToken ct) =>
        ReferenceHttp.Edited(context, await handler.HandleAsync(new(key, null, ifMatch), ct), noContent: true);
    private static async Task<IResult> ResetField(string key, string field, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<ResetSystemOverridesCommand, ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>> handler, HttpContext context, CancellationToken ct)
    {
        if (!Enum.GetNames<SystemOverrideField>().Contains(field, StringComparer.OrdinalIgnoreCase))
            return ProblemResults.Problem(400, "ReferenceResource.Invalid", "Unknown override field.");
        return ReferenceHttp.Edited(context, await handler.HandleAsync(new(key, Enum.Parse<SystemOverrideField>(field, true), ifMatch), ct), noContent: true);
    }
    private static async Task<IResult> Delete(string key, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<DeleteSystemCommand, Deleted> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(key, ifMatch), ct);
        return result.IsError ? ReferenceHttp.Failure(result.Errors) : Results.NoContent();
    }
}
