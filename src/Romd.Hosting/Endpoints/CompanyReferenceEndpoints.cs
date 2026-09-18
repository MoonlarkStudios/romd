using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.ReferenceData.Companies.Commands;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class CompanyReferenceEndpoints
{
    public static void MapCompanyReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("companies").WithTags("companies").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] ICompanyReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("List" + prefix + "Companies").Produces<IReadOnlyList<CompanyResourceDto>>();
        group.MapGet("{key}", async (string key, [FromServices] ICompanyReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(key, ct), false))
            .WithName("Get" + prefix + "Companies").Produces<CompanyResourceDto>().Produces(304).Produces(404);
        if (!admin)
            return;
        group.MapPost("", Create).WithName("CreateCompanies").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<CompanyResourceDto>(201).Produces(400).Produces(409);
        group.MapPatch("{key}", Update).WithName("PatchCompanies").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<CompanyResourceDto>().Produces(400).Produces(404).Produces(409).Produces(412).Produces(428);
        group.MapGet("{key}/overrides", async (string key, [FromServices] ICompanyReferenceReader reader, HttpContext context, CancellationToken ct) =>
            ReferenceHttp.Read(context, await reader.GetAsync(key, ct), true))
            .WithName("GetCompaniesOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<CompanyOverridesDto>().Produces(304).Produces(404);
        group.MapDelete("{key}/overrides", Reset).WithName("ResetCompaniesOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).Produces(404).Produces(409).Produces(412).Produces(428);
        group.MapDelete("{key}/overrides/{field}", ResetField).WithName("ResetCompaniesOverride").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).Produces(400).Produces(404).Produces(409).Produces(412).Produces(428);
        group.MapDelete("{key}", Delete).WithName("DeleteCompanies").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).Produces(404).Produces(409).Produces(412).Produces(428);
    }
    private static async Task<IResult> Create(CreateCompanyDto request, [FromServices] ICommandHandler<CreateCompanyCommand, ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>> handler, HttpContext context, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.Key, new(request.Name, request.Description)), ct);
        return ReferenceHttp.Edited(context, result, location: resource => "/api/companies/" + Uri.EscapeDataString(resource.Key));
    }
    private static async Task<IResult> Update(string key, CompanyPatchDto patch, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<UpdateCompanyCommand, ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>> handler, HttpContext context, CancellationToken ct) =>
        ReferenceHttp.Edited(context, await handler.HandleAsync(new(key, new(
            patch.Name,
            patch.Description, patch.Changes.ContainsKey("description")
        ), ifMatch), ct));
    private static async Task<IResult> Reset(string key, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<ResetCompanyOverridesCommand, ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>> handler, HttpContext context, CancellationToken ct) =>
        ReferenceHttp.Edited(context, await handler.HandleAsync(new(key, null, ifMatch), ct), noContent: true);
    private static async Task<IResult> ResetField(string key, string field, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<ResetCompanyOverridesCommand, ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>> handler, HttpContext context, CancellationToken ct)
    {
        if (!Enum.GetNames<CompanyOverrideField>().Contains(field, StringComparer.OrdinalIgnoreCase))
            return ProblemResults.Problem(400, "ReferenceResource.Invalid", "Unknown override field.");
        return ReferenceHttp.Edited(context, await handler.HandleAsync(new(key, Enum.Parse<CompanyOverrideField>(field, true), ifMatch), ct), noContent: true);
    }
    private static async Task<IResult> Delete(string key, [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromServices] ICommandHandler<DeleteCompanyCommand, Deleted> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(key, ifMatch), ct);
        return result.IsError ? ReferenceHttp.Failure(result.Errors) : Results.NoContent();
    }
}
