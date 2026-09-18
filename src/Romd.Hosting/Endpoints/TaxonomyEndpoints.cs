using ErrorOr;
using Romd.Admin.Application.Taxonomy.Queries.GetMergeImpact;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Taxonomy.Commands.AddLanguageAlias;
using Romd.Admin.Application.Taxonomy.Commands.AddRegionAlias;
using Romd.Admin.Application.Taxonomy.Commands.MergeLanguage;
using Romd.Admin.Application.Taxonomy.Commands.MergeRegion;
using Romd.Admin.Application.Taxonomy.Commands.RemoveLanguageAlias;
using Romd.Admin.Application.Taxonomy.Commands.RemoveRegionAlias;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguages;
using Romd.Admin.Application.Taxonomy.Queries.ListRegions;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Host.Authorization;
using TaxonomyContracts = Romd.Contracts.Management.Taxonomy;

namespace Romd.Host.Endpoints;

public static class TaxonomyEndpoints
{
    public static IEndpointRouteBuilder MapTaxonomyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/taxonomy/{kind}/{sourceId}/merge/{targetId}", GetMergeImpact)
            .WithName("GetTaxonomyMergeImpact").WithTags("Taxonomy")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<TaxonomyContracts.TaxonomyMergeImpactDto>().ProducesProblem(404);
        var regions = app.MapGroup("/taxonomy/regions")
            .WithTags("Taxonomy");

        regions.MapGet("", ListRegions)
            .WithName("ListRegions")
            .WithDescription("List all regions with their aliases")
            .Produces<IReadOnlyList<TaxonomyContracts.RegionDto>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        regions.MapPost("{sourceId}/merge/{targetId}", MergeRegions)
            .WithName("MergeRegions")
            .WithDescription("Merge source region into target region, moving all aliases and game associations")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        regions.MapPost("{regionId}/aliases", AddRegionAlias)
            .WithName("AddRegionAlias")
            .WithDescription("Add a new alias to a region")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        regions.MapDelete("{regionId}/aliases/{aliasId}", RemoveRegionAlias)
            .WithName("RemoveRegionAlias")
            .WithDescription("Remove an alias from a region")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        var languages = app.MapGroup("/taxonomy/languages")
            .WithTags("Taxonomy");

        languages.MapGet("", ListLanguages)
            .WithName("ListLanguages")
            .WithDescription("List all languages with their aliases")
            .Produces<IReadOnlyList<TaxonomyContracts.LanguageDto>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        languages.MapPost("{sourceId}/merge/{targetId}", MergeLanguages)
            .WithName("MergeLanguages")
            .WithDescription("Merge source language into target language, moving all aliases and game associations")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        languages.MapPost("{languageId}/aliases", AddLanguageAlias)
            .WithName("AddLanguageAlias")
            .WithDescription("Add a new alias to a language")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        languages.MapDelete("{languageId}/aliases/{aliasId}", RemoveLanguageAlias)
            .WithName("RemoveLanguageAlias")
            .WithDescription("Remove an alias from a language")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        return app;
    }

    private static async Task<IResult> GetMergeImpact(string kind, Sqid sourceId, Sqid targetId,
        IQueryHandler<GetMergeImpactQuery, TaxonomyContracts.TaxonomyMergeImpactDto> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(kind, sourceId, targetId), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> ListRegions(
        IQueryHandler<ListRegionsQuery, IReadOnlyList<RegionWithAliases>> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListRegionsQuery(), ct);
        return result.Match(
            regions => Results.Ok(regions.Select(ToRegionDto).ToList()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> MergeRegions(
        Sqid sourceId,
        Sqid targetId,
        ICommandHandler<MergeRegionCommand, Deleted> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new MergeRegionCommand(sourceId.Value, targetId.Value), ct);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> AddRegionAlias(
        Sqid regionId,
        TaxonomyContracts.AddAliasRequest request,
        ICommandHandler<AddRegionAliasCommand, Created> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AddRegionAliasCommand(regionId.Value, request.Alias), ct);
        return result.Match(
            _ => Results.Created(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> RemoveRegionAlias(
        Sqid regionId,
        Sqid aliasId,
        ICommandHandler<RemoveRegionAliasCommand, Deleted> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RemoveRegionAliasCommand(regionId.Value, aliasId.Value), ct);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> ListLanguages(
        IQueryHandler<ListLanguagesQuery, IReadOnlyList<LanguageWithAliases>> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListLanguagesQuery(), ct);
        return result.Match(
            languages => Results.Ok(languages.Select(ToLanguageDto).ToList()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> MergeLanguages(
        Sqid sourceId,
        Sqid targetId,
        ICommandHandler<MergeLanguageCommand, Deleted> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new MergeLanguageCommand(sourceId.Value, targetId.Value), ct);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> AddLanguageAlias(
        Sqid languageId,
        TaxonomyContracts.AddAliasRequest request,
        ICommandHandler<AddLanguageAliasCommand, Created> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AddLanguageAliasCommand(languageId.Value, request.Alias), ct);
        return result.Match(
            _ => Results.Created(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> RemoveLanguageAlias(
        Sqid languageId,
        Sqid aliasId,
        ICommandHandler<RemoveLanguageAliasCommand, Deleted> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RemoveLanguageAliasCommand(languageId.Value, aliasId.Value), ct);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static TaxonomyContracts.RegionDto ToRegionDto(RegionWithAliases region) => new()
    {
        Id = IdCoder.Encode(region.Id),
        Name = region.Name,
        SortOrder = region.SortOrder,
        IsAutoCreated = region.IsAutoCreated,
        CanMerge = region.CanMerge,
        Aliases = region.Aliases.Select(a => new TaxonomyContracts.AliasDto
        {
            Id = IdCoder.Encode(a.Id),
            Alias = a.Alias,
            Ownership = a.Ownership
        }).ToList()
    };

    private static TaxonomyContracts.LanguageDto ToLanguageDto(LanguageWithAliases language) => new()
    {
        Id = IdCoder.Encode(language.Id),
        Name = language.Name,
        Code = language.Code,
        SortOrder = language.SortOrder,
        IsAutoCreated = language.IsAutoCreated,
        CanMerge = language.CanMerge,
        Aliases = language.Aliases.Select(a => new TaxonomyContracts.AliasDto
        {
            Id = IdCoder.Encode(a.Id),
            Alias = a.Alias,
            Ownership = a.Ownership
        }).ToList()
    };

}
