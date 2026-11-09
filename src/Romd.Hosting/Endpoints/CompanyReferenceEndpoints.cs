using ErrorOr;
using System.ComponentModel.DataAnnotations;
using Romd.Contracts.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.ReferenceData.Companies.Commands;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Host.Authorization;

using ContractCompanyOverrides = Romd.Contracts.Common.ReferenceCatalog.CompanyOverrides;

namespace Romd.Host.Endpoints;

public static class CompanyReferenceEndpoints
{
    public static void MapCompanyReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var prefix = admin ? "Admin" : "Consumer";
        var group = app.MapGroup("companies").WithTags("companies").RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("", async ([FromServices] ICompanyReferenceReader reader, string? query = null, string? cursor = null,
            [Range(1, ReferencePageRequest.MaxLimit)] int limit = ReferencePageRequest.DefaultLimit, string? sortBy = null, CancellationToken ct = default) =>
        {
            var request = ReferencePageRequest.Create(prefix + ":companies", query, cursor, limit, sortBy);
            return request.IsError ? request.Errors.ToProblem() : Results.Ok(await reader.ListAsync(request.Value, ct));
        }).WithName("List" + prefix + "Companies")
            .WithDescription("Registered companies in ascending PostgreSQL C (UTF-8 byte) natural-key order. Bounded keyset traversal; query matches normalized display facts. Default/max limit 100; sortBy supports key:asc. Each page is one coherent observation, not a complete-traversal snapshot.")
            .Produces<Page<CompanyResource>>();
        group.MapGet("{companyKey}", async (string companyKey, [FromServices] ICompanyReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(companyKey, ct) is { } company
                ? Preconditions.ConditionalRead(context, company.Resource, company.Revision)
                : ReferenceCatalogEndpoints.UnknownReference())
            .WithName("Get" + prefix + "Company").Produces<CompanyResource>().ConditionalRead().ProducesProblems(404);
        if (!admin)
            return;
        group.MapPost("", Create).WithName("CreateCompany").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .ProducesCreated<CompanyResource>().ProducesProblems(400, 409).ConditionalCreate();
        group.MapPatch("{companyKey}", Update).WithName("UpdateCompany").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<CompanyResource>().ProducesProblems(400, 404, 409).ConditionalWrite();
        group.MapGet("{companyKey}/overrides", async (string companyKey, [FromServices] ICompanyReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(companyKey, ct) is { } company
                ? Preconditions.ConditionalRead(context, company.Overrides, company.Revision)
                : ReferenceCatalogEndpoints.UnknownReference())
            .WithName("GetCompanyOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin).Produces<ContractCompanyOverrides>()
            .ConditionalRead().ProducesProblems(404);
        group.MapDelete("{companyKey}/overrides", Reset).WithName("DeleteCompanyOverrides").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).ProducesProblems(404, 409).ConditionalWrite();
        group.MapDelete("{companyKey}/overrides/{field}", ResetField).WithName("DeleteCompanyOverride").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).ProducesProblems(400, 404, 409).ConditionalWrite();
        group.MapDelete("{companyKey}", Delete).WithName("DeleteCompany").RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(204).ProducesProblems(404, 409).ConditionalWriteWithoutETag();
    }
    private static async Task<IResult> Create(CreateCompany request, [FromServices] ICommandHandler<CreateCompanyCommand, ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>> handler, HttpContext context, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.Key, new(request.Name, request.Description)), ct);
        return result.Match(
            created => Results.Created("/api/companies/" + Uri.EscapeDataString(created.Resource.Key), created.Resource).WithETag(context, created.Revision),
            errors => errors.ToProblem());
    }
    private static async Task<IResult> Update(string companyKey, CompanyPatch patch, HttpContext context,
        [FromServices] ICommandHandler<UpdateCompanyCommand, ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>> handler, CancellationToken ct)
    {
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(companyKey, new(
            patch.Name,
            patch.Description, patch.Changes.ContainsKey("description")
        ), ifMatch.Value), ct);
        return result.Match(updated => Results.Ok(updated.Resource).WithETag(context, updated.Revision), errors => errors.ToProblem());
    }
    private static async Task<IResult> Reset(string companyKey, HttpContext context,
        [FromServices] ICommandHandler<ResetCompanyOverridesCommand, ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>> handler, CancellationToken ct)
    {
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(companyKey, null, ifMatch.Value), ct);
        return result.Match(reset => Results.NoContent().WithETag(context, reset.Revision), errors => errors.ToProblem());
    }
    private static async Task<IResult> ResetField(string companyKey, string field, HttpContext context,
        [FromServices] ICommandHandler<ResetCompanyOverridesCommand, ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>> handler, CancellationToken ct)
    {
        if (!Enum.GetNames<CompanyOverrideField>().Contains(field, StringComparer.OrdinalIgnoreCase))
            return ProblemResults.Problem(400, "ReferenceResource.Invalid", "Unknown override field.");
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(companyKey, Enum.Parse<CompanyOverrideField>(field, true), ifMatch.Value), ct);
        return result.Match(reset => Results.NoContent().WithETag(context, reset.Revision), errors => errors.ToProblem());
    }
    private static async Task<IResult> Delete(string companyKey, HttpContext context,
        [FromServices] ICommandHandler<DeleteCompanyCommand, Deleted> handler, CancellationToken ct)
    {
        var ifMatch = Preconditions.IfMatch(context, ReferenceCatalogEndpoints.PreconditionArea);
        if (ifMatch.IsError)
            return ifMatch.Errors.ToProblem();
        var result = await handler.HandleAsync(new(companyKey, ifMatch.Value), ct);
        return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
    }
}
