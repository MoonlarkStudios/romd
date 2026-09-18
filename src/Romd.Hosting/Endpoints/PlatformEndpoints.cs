using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Source;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Queries.GetDatsByPlatform;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.AddPlatformAlias;
using Romd.Admin.Application.Source.Platform.Commands.RemovePlatformAlias;
using Romd.Admin.Application.Catalog.Queries.GetPlatformBios;
using Romd.Admin.Application.Source.Platform.Queries.GetPlatformAliases;
using Romd.Admin.Application.Titles;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for Platform operations.
/// </summary>
public static class PlatformEndpoints
{
    /// <summary>
    ///     Maps all Platform-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/systems")
            .WithTags("Platforms");


        group.MapGet("{systemKey}/aliases", GetAliases)
            .WithName("ListPlatformAliases")
            .WithDescription("List a platform's name aliases and provider mappings")
            .Produces<IEnumerable<Models.PlatformAlias>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("{systemKey}/aliases", AddAlias)
            .WithName("AddPlatformAlias")
            .WithDescription("Add a name alias or provider mapping to a platform")
            .Produces<Models.PlatformAlias>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapDelete("{systemKey}/aliases/{aliasId}", RemoveAlias)
            .WithName("RemovePlatformAlias")
            .WithDescription("Remove an alias from a platform")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);


        group.MapGet("{systemKey}/titles", GetTitlesByPlatform)
            .WithName("ListTitlesByPlatform")
            .Produces<IEnumerable<Models.Title>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{systemKey}/local-payload", GetTitlesWithLocalPayloadByPlatform)
            .WithName("ListTitlesWithLocalPayloadByPlatform")
            .Produces<IEnumerable<Models.Title>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{systemKey}/dats", GetDatsByPlatform)
            .WithName("ListDatsByPlatform")
            .Produces<IEnumerable<Models.Dat>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{systemKey}/bios", GetBiosByPlatform)
            .WithName("ListBiosByPlatform")
            .WithDescription("List a platform's BIOS/firmware entries with ownership status")
            .Produces<IEnumerable<Models.Bios>>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static async Task<IResult> GetAliases(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetPlatformAliasesQuery, IReadOnlyList<Domain.Source.Platform.PlatformAlias>> handler,
        CancellationToken cancellationToken)
    {
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0;
        if (platformId == 0) return Results.NotFound();
        var result = await handler.HandleAsync(new GetPlatformAliasesQuery(platformId), cancellationToken);
        return result.Match(
            aliases => Results.Ok(aliases.ToContract()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> AddAlias(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        Models.AddPlatformAliasRequest request,
        ICommandHandler<AddPlatformAliasCommand, Domain.Source.Platform.PlatformAlias> handler,
        CancellationToken cancellationToken)
    {
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0;
        if (platformId == 0) return Results.NotFound();
        var commandResult = AddPlatformAliasCommand.Create(platformId, request.Type, request.Value, request.Provider);
        if (commandResult.IsError)
        {
            return commandResult.Errors.ToProblem();
        }

        var result = await handler.HandleAsync(commandResult.Value, cancellationToken);
        return result.Match(
            alias => Results.Created(
                $"/api/systems/{Uri.EscapeDataString(systemKey)}/aliases/{IdCoder.Encode(alias.Id)}",
                alias.ToContract()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> RemoveAlias(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        Sqid aliasId,
        ICommandHandler<RemovePlatformAliasCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0;
        if (platformId == 0) return Results.NotFound();
        var result = await handler.HandleAsync(new RemovePlatformAliasCommand(platformId, aliasId), cancellationToken);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetTitlesByPlatform(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IPlatformRepository platformRepository,
        ITitleRepository titleRepository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0;
        if (platformId == 0) return Results.NotFound();
        // Verify platform exists
        var platform = await platformRepository.GetByIdAsync(platformId, cancellationToken);
        if (platform is null)
        {
            return Results.NotFound();
        }

        int? libraryId = ResolveLibraryId(currentUser);
        var titles = await titleRepository.GetByPlatformFilteredAsync(platformId, libraryId, cancellationToken);

        // Read the materialized local-payload fact for all returned titles in one batch query.
        var titleIdsWithLocalPayload = await titleRepository.GetTitleIdsWithLocalPayloadAsync(
            titles.Select(t => t.Id), cancellationToken);

        return Results.Ok(titles.ToContract(systemKeys, titleIdsWithLocalPayload));
    }

    private static async Task<IResult> GetTitlesWithLocalPayloadByPlatform(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IPlatformRepository platformRepository,
        ITitleRepository titleRepository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0;
        if (platformId == 0) return Results.NotFound();
        // Verify platform exists
        var platform = await platformRepository.GetByIdAsync(platformId, cancellationToken);
        if (platform is null)
        {
            return Results.NotFound();
        }

        int? libraryId = ResolveLibraryId(currentUser);
        var titles = await titleRepository.GetWithLocalPayloadByPlatformFilteredAsync(
            platformId, libraryId, cancellationToken);
        return Results.Ok(titles.ToContractWithLocalPayload(systemKeys));
    }

    /// <summary>
    ///     Resolves the library ID for the current user.
    ///     Contributor+ users bypass filtering (returns null).
    /// </summary>
    private static int? ResolveLibraryId(ICurrentUser currentUser) =>
        currentUser.HasRole(RomdRoleType.Contributor) ? null : currentUser.LibraryId;

    private static async Task<IResult> GetDatsByPlatform(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetDatsByPlatformQuery, IReadOnlyList<DatWithSourceStatus>> handler,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0;
        if (platformId == 0) return Results.NotFound();
        var result = await handler.HandleAsync(new GetDatsByPlatformQuery(platformId), cancellationToken);
        return result.Match(
            dats => Results.Ok(dats.ToContract(systemKeys)),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetBiosByPlatform(
        string systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetPlatformBiosQuery, IReadOnlyList<BiosOwnership>> handler,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        int platformId = await referenceCatalog.ResolveSystemIdAsync(systemKey, cancellationToken) ?? 0;
        if (platformId == 0) return Results.NotFound();
        var result = await handler.HandleAsync(new GetPlatformBiosQuery(platformId), cancellationToken);
        return result.Match(
            bios => Results.Ok(bios.ToContract(systemKeys)),
            errors => errors.ToProblem());
    }
}
