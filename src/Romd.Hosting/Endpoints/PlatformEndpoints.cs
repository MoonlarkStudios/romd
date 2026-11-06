using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Pagination;
using System.Globalization;
using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Source;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Dat.Queries.GetDatsByPlatform;
using Romd.Admin.Application.Source.Platform.Commands.AddPlatformAlias;
using Romd.Admin.Application.Source.Platform.Commands.RemovePlatformAlias;
using Romd.Admin.Application.Catalog.Queries.GetPlatformBios;
using Romd.Admin.Application.Source.Platform.Queries.GetPlatformAliases;
using Romd.Admin.Application.Titles;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
using CommonModels = Romd.Contracts.Common.Models;
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
            .WithName("ListSystemAliases")
            .WithDescription("List a platform's name aliases and provider mappings")
            .Produces<CommonModels.Page<Models.PlatformAlias>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("{systemKey}/aliases", AddAlias)
            .WithName("CreateSystemAlias")
            .WithDescription("Add a name alias or provider mapping to a platform")
            .ProducesCreated<Models.PlatformAlias>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapDelete("{systemKey}/aliases/{aliasId}", RemoveAlias)
            .WithName("DeleteSystemAlias")
            .WithDescription("Remove an alias from a platform")
            .Produces(StatusCodes.Status204NoContent).ProducesProblems(400, 404)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);


        group.MapGet("{systemKey}/titles", GetTitlesByPlatform)
            .WithName("ListSystemTitles")
            .WithDescription($"Returns titles ordered by name and ID. Limit defaults to {PlatformTitlePageRequest.DefaultLimit} and must be between 1 and {PlatformTitlePageRequest.MaxLimit}. Continuation cursors are bound to the system, library, and payload scope.")
            .Produces<CommonModels.Page<Models.Title>>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{systemKey}/local-payload", GetTitlesWithLocalPayloadByPlatform)
            .WithName("ListSystemLocalPayloadTitles")
            .WithDescription($"Returns titles with local payload ordered by name and ID. Limit defaults to {PlatformTitlePageRequest.DefaultLimit} and must be between 1 and {PlatformTitlePageRequest.MaxLimit}. Continuation cursors are bound to the system, library, and payload scope.")
            .Produces<CommonModels.Page<Models.Title>>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{systemKey}/dats", GetDatsByPlatform)
            .WithName("ListSystemDats")
            .Produces<CommonModels.Page<Models.Dat>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{systemKey}/bios", GetBiosByPlatform)
            .WithName("ListSystemBios")
            .WithDescription("List a platform's BIOS/firmware entries with ownership status")
            .Produces<CommonModels.Page<Models.Bios>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static async Task<IResult> GetAliases(SystemKeyParameter systemKey, IPlatformAliasRepository repository,
        string? cursor = null, [Range(1, 100)] int limit = 50, CancellationToken cancellationToken = default)
    {
        string fingerprint = KeysetCursor.Fingerprint("admin.system.aliases", systemKey.Key, "id:asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return ProblemResults.ValidationProblem("Request.InvalidParameter", "cursor", "The cursor is invalid for this query.");
        var result = await repository.ListPageAsync(systemKey.SystemId, position.Value?.Id, limit, cancellationToken);
        return Results.Ok(new CommonModels.Page<Models.PlatformAlias>
        {
            Items = result.Items.ToContract().ToArray(),
            HasNextPage = result.HasNextPage,
            NextCursor = result.HasNextPage ? KeysetCursor.Encode(new IntegerPosition(int.Parse(result.NextCursor!, CultureInfo.InvariantCulture)), fingerprint) : null
        });
    }

    private static async Task<IResult> AddAlias(
        SystemKeyParameter systemKey,
        Models.AddPlatformAliasRequest request,
        ICommandHandler<AddPlatformAliasCommand, Domain.Source.Platform.PlatformAlias> handler,
        CancellationToken cancellationToken)
    {
        var commandResult = AddPlatformAliasCommand.Create(systemKey.SystemId, request.Type, request.Value, request.Provider);
        if (commandResult.IsError)
        {
            return commandResult.Errors.ToProblem();
        }

        var result = await handler.HandleAsync(commandResult.Value, cancellationToken);
        return result.Match(
            alias => Results.Created(
                $"/api/systems/{Uri.EscapeDataString(systemKey.Key)}/aliases/{IdCoder.Encode(alias.Id)}",
                alias.ToContract()),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> RemoveAlias(
        SystemKeyParameter systemKey,
        Sqid aliasId,
        ICommandHandler<RemovePlatformAliasCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RemovePlatformAliasCommand(systemKey.SystemId, aliasId), cancellationToken);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static Task<IResult> GetTitlesByPlatform(
        SystemKeyParameter systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        ITitleReader titleReader, ICurrentUser currentUser,
        string? cursor = null, [Range(1, PlatformTitlePageRequest.MaxLimit)] int limit = PlatformTitlePageRequest.DefaultLimit,
        CancellationToken cancellationToken = default) =>
        ReadTitlesByPlatform(systemKey, referenceCatalog, titleReader, currentUser,
            false, cursor, limit, cancellationToken);

    private static Task<IResult> GetTitlesWithLocalPayloadByPlatform(
        SystemKeyParameter systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        ITitleReader titleReader, ICurrentUser currentUser,
        string? cursor = null, [Range(1, PlatformTitlePageRequest.MaxLimit)] int limit = PlatformTitlePageRequest.DefaultLimit,
        CancellationToken cancellationToken = default) =>
        ReadTitlesByPlatform(systemKey, referenceCatalog, titleReader, currentUser,
            true, cursor, limit, cancellationToken);

    private static async Task<IResult> ReadTitlesByPlatform(
        SystemKeyParameter systemKey, IReferenceCatalogService referenceCatalog,
        ITitleReader titleReader, ICurrentUser currentUser,
        bool localPayloadOnly, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var request = PlatformTitlePageRequest.Create(systemKey.SystemId, ResolveLibraryId(currentUser), localPayloadOnly, cursor, limit);
        if (request.IsError)
            return request.Errors.ToProblem();
        var page = await titleReader.ListByPlatformAsync(request.Value, cancellationToken);
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        return Results.Ok(new CommonModels.Page<Models.Title>
        {
            Items = page.Items.ToContract(systemKeys).ToList(),
            NextCursor = page.NextCursor,
            HasNextPage = page.HasNextPage
        });
    }

    /// <summary>
    ///     Resolves the library ID for the current user.
    ///     Contributor+ users bypass filtering (returns null).
    /// </summary>
    private static int? ResolveLibraryId(ICurrentUser currentUser) =>
        currentUser.HasRole(RomdRoleType.Contributor) ? null : currentUser.LibraryId ?? 0;

    private static async Task<IResult> GetDatsByPlatform(SystemKeyParameter systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IQueryHandler<GetDatsByPlatformQuery, PagedList<DatWithSourceStatus>> handler,
        string? cursor = null, [Range(1, 100)] int limit = 50, string? query = null, CancellationToken cancellationToken = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var result = await handler.HandleAsync(new GetDatsByPlatformQuery(systemKey.SystemId, cursor, limit, query), cancellationToken);
        return result.Match(page => Results.Ok(new CommonModels.Page<Models.Dat>
        {
            Items = page.Items.ToContract(systemKeys).ToArray(),
            NextCursor = page.NextCursor,
            HasNextPage = page.HasNextPage
        }), errors => errors.ToProblem());
    }

    private static async Task<IResult> GetBiosByPlatform(SystemKeyParameter systemKey, [FromServices] IReferenceCatalogService referenceCatalog,
        IBiosRepository repository, string? cursor = null, [Range(1, 100)] int limit = 50, CancellationToken cancellationToken = default)
    {
        string fingerprint = KeysetCursor.Fingerprint("admin.system.bios", systemKey.Key, "id:asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return ProblemResults.ValidationProblem("Request.InvalidParameter", "cursor", "The cursor is invalid for this query.");
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var result = await repository.ListPageAsync(systemKey.SystemId, position.Value?.Id, limit, cancellationToken);
        return Results.Ok(new CommonModels.Page<Models.Bios>
        {
            Items = result.Items.ToContract(systemKeys).ToArray(),
            HasNextPage = result.HasNextPage,
            NextCursor = result.HasNextPage ? KeysetCursor.Encode(new IntegerPosition(int.Parse(result.NextCursor!, CultureInfo.InvariantCulture)), fingerprint) : null
        });
    }
}
