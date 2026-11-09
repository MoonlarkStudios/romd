using System.ComponentModel.DataAnnotations;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.Commands.AddLanguageAlias;
using Romd.Admin.Application.Taxonomy.Commands.MergeLanguage;
using Romd.Admin.Application.Taxonomy.Commands.RemoveLanguageAlias;
using Romd.Admin.Application.Taxonomy.Queries.GetLanguage;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguageAliases;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguages;
using Romd.Admin.Application.Taxonomy.Queries.PreviewLanguageMerge;
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
///     Languages on both hosts. The consumer serves the registered reference projection; the admin host serves the
///     management projection of every row, with merge and alias management under the language.
/// </summary>
public static class LanguageReferenceEndpoints
{
    public static void MapLanguageReferences(this IEndpointRouteBuilder app, bool admin)
    {
        var group = app.MapGroup("languages").WithTags("languages").RequireAuthorization(AuthorizationPolicies.RequireUser);
        if (admin)
        {
            MapManagement(group);
            return;
        }

        group.MapGet("", async ([FromServices] ILanguageReferenceReader reader, CancellationToken ct) => Results.Ok(await reader.ListAsync(ct)))
            .WithName("ListLanguages")
            .WithDescription("Registered languages only; bounded by the built-in reference catalog.")
            .Produces<IReadOnlyList<LanguageResource>>();
        group.MapGet("{languageKey}", async (string languageKey, [FromServices] ILanguageReferenceReader reader, HttpContext context, CancellationToken ct) =>
            await reader.GetAsync(languageKey, ct) is { } found
                ? Preconditions.ConditionalRead(context, found.Resource, found.Revision)
                : ReferenceCatalogEndpoints.UnknownReference())
            .WithName("GetLanguage").Produces<LanguageResource>().ConditionalRead().ProducesProblems(404);
    }

    private static void MapManagement(RouteGroupBuilder group)
    {
        group.MapGet("", List)
            .WithName("ListLanguages")
            .WithDescription("Every language, registered or created from a DAT token, ordered by sort order, name and key, each " +
                             $"embedding its first {TaxonomyAliasPageRequest.EmbeddedLimit} aliases. A query matches normalized names, keys, codes " +
                             "and every alias, embedded or not. Limit defaults to " +
                             $"{TaxonomyPageRequest.DefaultLimit} and must be between 1 and {TaxonomyPageRequest.MaxLimit}; a cursor " +
                             "continues only this collection and query.")
            .Produces<Page<Language>>();
        group.MapGet("{languageKey}", Get)
            .WithName("GetLanguage")
            .WithDescription($"The language with its first {TaxonomyAliasPageRequest.EmbeddedLimit} aliases; its validator versions " +
                             "exactly that representation, not aliases beyond the embedded page.")
            .Produces<Language>().ConditionalRead().ProducesProblems(404);
        group.MapGet("{languageKey}/aliases", ListAliases)
            .WithName("ListLanguageAliases")
            .WithDescription($"The language's aliases in ascending id order. Limit defaults to {TaxonomyAliasPageRequest.DefaultLimit} and " +
                             $"must be between 1 and {TaxonomyAliasPageRequest.MaxLimit}; a cursor, including the one the language embeds, " +
                             "continues only this language's aliases. A merge moves aliases with their ids, so a traversal running across a " +
                             "merge into the language never repeats an alias but may miss one; start again from the first page to see them all.")
            .Produces<Page<AliasResource>>().ProducesProblems(404);
        group.MapPost("{languageKey}/merge", Merge)
            .WithName("MergeLanguage")
            .WithDescription("Merge an unregistered language into the target, moving its aliases and game associations, then delete it.")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces(StatusCodes.Status204NoContent).ProducesProblems(400, 404, 409);
        group.MapPost("{languageKey}/merge/preview", PreviewMerge)
            .WithName("PreviewLanguageMerge")
            .WithDescription("What merging the language into the target would move, refused exactly where the merge is refused. Changes nothing.")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<TaxonomyMergeImpact>().ProducesProblems(400, 404, 409);
        group.MapPost("{languageKey}/aliases", CreateAlias)
            .WithName("CreateLanguageAlias")
            .WithDescription("Add an installation-owned alias that DAT tokens resolve to this language.")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .ProducesCreated<AliasResource>().ProducesProblems(400, 404, 409);
        group.MapDelete("{languageKey}/aliases/{aliasId}", DeleteAlias)
            .WithName("DeleteLanguageAlias")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            // A malformed aliasId fails binding with 400.
            .Produces(StatusCodes.Status204NoContent).ProducesProblems(400, 404, 409);
    }

    private static async Task<IResult> List(IQueryHandler<ListLanguagesQuery, PagedList<TaxonomyEntry>> handler,
        string? query = null, string? cursor = null, [Range(1, TaxonomyPageRequest.MaxLimit)] int limit = TaxonomyPageRequest.DefaultLimit,
        CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new ListLanguagesQuery(cursor, limit, query), ct);
        return result.Match(
            page => Results.Ok(new Page<Language> { Items = page.Items.Select(ToDto).ToList(), NextCursor = page.NextCursor, HasNextPage = page.HasNextPage }),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> Get(string languageKey, IQueryHandler<GetLanguageQuery, TaxonomyEntry> handler,
        HttpContext context, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetLanguageQuery(languageKey), ct);
        return result.Match(entry => Preconditions.ConditionalRead(context, ToDto(entry), entry.Revision), errors => errors.ToProblem());
    }

    private static async Task<IResult> ListAliases(string languageKey, IQueryHandler<ListLanguageAliasesQuery, PagedList<TaxonomyAlias>> handler,
        string? cursor = null, [Range(1, TaxonomyAliasPageRequest.MaxLimit)] int limit = TaxonomyAliasPageRequest.DefaultLimit,
        CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new ListLanguageAliasesQuery(languageKey, cursor, limit), ct);
        return result.Match(page => Results.Ok(TaxonomyResponses.ToDto(page)), errors => errors.ToProblem());
    }

    private static async Task<IResult> Merge(string languageKey, MergeTaxonomyRequest request,
        ICommandHandler<MergeLanguageCommand, Deleted> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new MergeLanguageCommand(languageKey, request.TargetKey), ct);
        return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
    }

    private static async Task<IResult> PreviewMerge(string languageKey, MergeTaxonomyRequest request,
        IQueryHandler<PreviewLanguageMergeQuery, TaxonomyMergeImpact> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new PreviewLanguageMergeQuery(languageKey, request.TargetKey), ct);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

    private static async Task<IResult> CreateAlias(string languageKey, AddAliasRequest request,
        ICommandHandler<AddLanguageAliasCommand, TaxonomyAlias> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AddLanguageAliasCommand(languageKey, request.Alias), ct);
        return result.Match(
            alias => Results.Created(TaxonomyResponses.AliasLocation("languages", languageKey, alias), TaxonomyResponses.ToDto(alias)),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> DeleteAlias(string languageKey, Sqid aliasId,
        ICommandHandler<RemoveLanguageAliasCommand, Deleted> handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RemoveLanguageAliasCommand(languageKey, aliasId.Value), ct);
        return result.Match(_ => Results.NoContent(), errors => errors.ToProblem());
    }

    private static Language ToDto(TaxonomyEntry entry) => new()
    {
        Key = entry.Key,
        Ownership = entry.Ownership,
        BuiltInVersion = entry.BuiltInVersion,
        Name = entry.Name,
        Description = entry.Description,
        SortOrder = entry.SortOrder,
        Code = entry.Code!,
        Retired = entry.Retired,
        IsAutoCreated = entry.IsAutoCreated,
        CanMerge = entry.CanMerge,
        Aliases = TaxonomyResponses.ToDto(ListLanguagesQueryHandler.Collection, entry.Key, entry.Aliases)
    };
}
