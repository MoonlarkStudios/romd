using System.ComponentModel.DataAnnotations;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.Commands.AddRegionAlias;
using Romd.Admin.Application.Taxonomy.Commands.MergeRegion;
using Romd.Admin.Application.Taxonomy.Commands.RemoveRegionAlias;
using Romd.Admin.Application.Taxonomy.Queries.GetRegion;
using Romd.Admin.Application.Taxonomy.Queries.ListRegionAliases;
using Romd.Admin.Application.Taxonomy.Queries.ListRegions;
using Romd.Admin.Application.Taxonomy.Queries.PreviewRegionMerge;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Management.Taxonomy;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

/// <summary>
///     Regions on both hosts. The consumer serves the registered reference projection; the admin host serves the
///     management projection of every row, with merge and alias management under the region.
/// </summary>
public static class RegionReferenceEndpoints
{
    public static void MapRegionReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var group = app.MapGroup("regions").WithTags("regions").RequireAuthorization(AuthorizationPolicies.RequireUser);
        if (admin)
        {
            MapManagement(group);
            return;
        }

        group.MapGet("", async ([FromServices] IRegionReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("ListRegions")
            .WithDescription("Registered regions only; bounded by the built-in reference catalog.")
            .Produces<IReadOnlyList<RegionResource>>();
        group.MapGet("{regionKey}", async (string regionKey, [FromServices] IRegionReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(regionKey, ct) is { } found
                ? Preconditions.ConditionalRead(context, found.Resource, found.Revision)
                : ReferenceCatalogEndpoints.UnknownReference())
            .WithName("GetRegion").Produces<RegionResource>().ConditionalRead().ProducesProblems(404);
    }

    private static void MapManagement(RouteGroupBuilder group)
    {
        group.MapGet("", List)
            .WithName("ListRegions")
            .WithDescription("Every region, registered or created from a DAT token, ordered by sort order, name and key, each " +
                             $"embedding its first {TaxonomyAliasPageRequest.EmbeddedLimit} aliases. A query matches normalized names, keys " +
                             "and every alias, embedded or not. Limit defaults to " +
                             $"{TaxonomyPageRequest.DefaultLimit} and must be between 1 and {TaxonomyPageRequest.MaxLimit}; a cursor " +
                             "continues only this collection and query.")
            .Produces<Page<Region>>();
        group.MapGet("{regionKey}", Get)
            .WithName("GetRegion")
            .WithDescription($"The region with its first {TaxonomyAliasPageRequest.EmbeddedLimit} aliases; its validator versions " +
                             "exactly that representation, not aliases beyond the embedded page.")
            .Produces<Region>().ConditionalRead().ProducesProblems(404);
        group.MapGet("{regionKey}/aliases", ListAliases)
            .WithName("ListRegionAliases")
            .WithDescription($"The region's aliases in ascending id order. Limit defaults to {TaxonomyAliasPageRequest.DefaultLimit} and " +
                             $"must be between 1 and {TaxonomyAliasPageRequest.MaxLimit}; a cursor, including the one the region embeds, " +
                             "continues only this region's aliases. A merge moves aliases with their ids, so a traversal running across a " +
                             "merge into the region never repeats an alias but may miss one; start again from the first page to see them all.")
            .Produces<Page<AliasResource>>().ProducesProblems(404);
        group.MapPost("{regionKey}/merge", Merge)
            .WithName("MergeRegion")
            .WithDescription("Merge an unregistered region into the target, moving its aliases and game associations, then delete it.")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(StatusCodes.Status204NoContent).ProducesProblems(400, 404, 409);
        group.MapPost("{regionKey}/merge/preview", PreviewMerge)
            .WithName("PreviewRegionMerge")
            .WithDescription("What merging the region into the target would move, refused exactly where the merge is refused. Changes nothing.")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<TaxonomyMergeImpact>().ProducesProblems(400, 404, 409);
        group.MapPost("{regionKey}/aliases", CreateAlias)
            .WithName("CreateRegionAlias")
            .WithDescription("Add an installation-owned alias that DAT tokens resolve to this region.")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .ProducesCreated<AliasResource>().ProducesProblems(400, 404, 409);
        group.MapDelete("{regionKey}/aliases/{aliasId}", DeleteAlias)
            .WithName("DeleteRegionAlias")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            // A malformed aliasId fails binding with 400.
            .Produces(StatusCodes.Status204NoContent).ProducesProblems(400, 404, 409);
    }

    private static async Task<IResult> List(IQueryHandler<ListRegionsQuery, PagedList<TaxonomyEntry>> handler,
        string? query = null, string? cursor = null, [Range(1, TaxonomyPageRequest.MaxLimit)] int limit = TaxonomyPageRequest.DefaultLimit,
        CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new ListRegionsQuery(cursor, limit, query), ct);
        return result.Match(
            page => Results.Ok(new Page<Region> { Items = page.Items.Select(ToDto).ToList(), NextCursor = page.NextCursor, HasNextPage = page.HasNextPage }),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> Get(string regionKey, IQueryHandler<GetRegionQuery, TaxonomyEntry> handler,
        HttpContext context, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetRegionQuery(regionKey), ct);
        return result.Match(entry => Preconditions.ConditionalRead(context, ToDto(entry), entry.Revision), errors => errors.ToProblem());
    }

    private static async Task<IResult> ListAliases(string regionKey, IQueryHandler<ListRegionAliasesQuery, PagedList<TaxonomyAlias>> handler,
        string? cursor = null, [Range(1, TaxonomyAliasPageRequest.MaxLimit)] int limit = TaxonomyAliasPageRequest.DefaultLimit,
        CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new ListRegionAliasesQuery(regionKey, cursor, limit), ct);
        return result.Match(page => Results.Ok(TaxonomyResponses.ToDto(page)), errors => errors.ToProblem());
    }

    private static async Task<IResult> Merge(string regionKey, MergeTaxonomyRequest request,
        ICommandHandler<MergeRegionCommand, Deleted> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new MergeRegionCommand(regionKey, request.TargetKey), ct);
        return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
    }

    private static async Task<IResult> PreviewMerge(string regionKey, MergeTaxonomyRequest request,
        IQueryHandler<PreviewRegionMergeQuery, TaxonomyMergeImpact> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new PreviewRegionMergeQuery(regionKey, request.TargetKey), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> CreateAlias(string regionKey, AddAliasRequest request,
        ICommandHandler<AddRegionAliasCommand, TaxonomyAlias> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AddRegionAliasCommand(regionKey, request.Alias), ct);
        return result.Match(
            alias => Results.Created(TaxonomyResponses.AliasLocation("regions", regionKey, alias), TaxonomyResponses.ToDto(alias)),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> DeleteAlias(string regionKey, Sqid aliasId,
        ICommandHandler<RemoveRegionAliasCommand, Deleted> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RemoveRegionAliasCommand(regionKey, aliasId.Value), ct);
        return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
    }

    private static Region ToDto(TaxonomyEntry entry) => new()
    {
        Key = entry.Key,
        Ownership = entry.Ownership,
        BuiltInVersion = entry.BuiltInVersion,
        Name = entry.Name,
        Description = entry.Description,
        SortOrder = entry.SortOrder,
        Retired = entry.Retired,
        IsAutoCreated = entry.IsAutoCreated,
        CanMerge = entry.CanMerge,
        Aliases = TaxonomyResponses.ToDto(ListRegionsQueryHandler.Collection, entry.Key, entry.Aliases)
    };
}
