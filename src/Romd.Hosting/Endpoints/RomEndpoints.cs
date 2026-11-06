using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Source.Rom.Commands.PurgeUnidentified;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Commands;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
using CommonModels = Romd.Contracts.Common.Models;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for ROM file operations.
/// </summary>
public static class RomEndpoints
{
    /// <summary>The largest ROM page; the repository serves whatever the endpoint admits.</summary>
    private const int _listMaxLimit = 100;

    public static IEndpointRouteBuilder MapRomEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/roms")
            .WithTags("Roms");

        group.MapGet("", List)
            .WithName("ListRoms")
            .Produces<CommonModels.Page<Models.Rom>>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("stats", GetStats)
            .WithName("GetRomStats")
            .Produces<Models.LibraryStats>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{romId}", GetById)
            .WithName("GetRom")
            .Produces<Models.Rom>()
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{romId}/download", Download)
            .WithName("DownloadRom")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblems(StatusCodes.Status403Forbidden)
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapDelete("{romId}", Delete)
            .WithName("DeleteRom")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Batch operations
        group.MapPost("batch-delete", BatchDelete)
            .WithName("BatchDeleteRoms")
            .Produces<BatchDeleteResponse>().ProducesProblems(400)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("unidentified/purge", PurgeUnidentified)
            .WithName("PurgeUnidentifiedRoms")
            .WithDescription("Delete every unidentified ROM (the inbox) in one operation.")
            .Produces<PurgeUnidentifiedResponse>()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<IResult> List(
        IRomRepository romRepository,
        [ClosedValues("cataloged", "unrouted", "unidentified")] string? status = null,
        string? cursor = null,
        [Range(1, _listMaxLimit)] int limit = 50,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        if (query?.Length > 200)
            return ProblemResults.ValidationProblem("Request.InvalidParameter", "query", "Filename query must be 200 characters or fewer.");
        string fingerprint = KeysetCursor.Fingerprint("admin.roms", status, query?.Trim().ToLowerInvariant(), "id:asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return ProblemResults.ValidationProblem("Request.InvalidParameter", "cursor", "The cursor is invalid for this query.");
        RomCatalogStatus? statusFilter = status switch
        {
            "cataloged" => RomCatalogStatus.Cataloged,
            "unrouted" => RomCatalogStatus.Unrouted,
            "unidentified" => RomCatalogStatus.Unidentified,
            _ => null
        };

        var result = await romRepository.ListAsync(
            statusFilter,
            position.Value?.Id.ToString(CultureInfo.InvariantCulture),
            limit,
            cancellationToken,
            query);

        var page = new CommonModels.Page<Models.Rom>
        {
            Items = result.Items.Select(x => x.Rom.ToContract(x.Status)).ToList(),
            NextCursor = result.HasNextPage && result.NextCursor is { } next
                ? KeysetCursor.Encode(new IntegerPosition(int.Parse(next, CultureInfo.InvariantCulture)), fingerprint) : null,
            HasNextPage = result.HasNextPage
        };

        return Results.Ok(page);
    }

    private static async Task<IResult> GetStats([FromServices] IReferenceCatalogService referenceCatalog,
        IRomRepository romRepository,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var stats = await romRepository.GetStatsAsync(cancellationToken);
        return Results.Ok(stats.ToContract(systemKeys));
    }

    private static async Task<IResult> GetById([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid romId,
        IRomRepository romRepository,
        IRomContentCatalog contentCatalog,
        [FromServices] IRomChdDeliveryAdmission chdAdmission,
        [FromServices] RomDeliveryPolicy deliveryPolicy,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var rom = await romRepository.GetByIdAsync(romId, cancellationToken);
        if (rom is null)
        {
            return new[] { CatalogErrors.RomNotFound() }.ToProblem();
        }

        var status = await romRepository.GetStatusAsync(rom.Id, cancellationToken);
        var matches = await romRepository.GetMatchesAsync(rom.Id, cancellationToken);
        var content = await contentCatalog.FindByRomIdAsync(rom.Id, cancellationToken);
        // A ROM has no platform of its own: its kind resolves under the platform of each matched DAT entry.
        var platforms = matches.Select(match => systemKeys.Optional(match.PlatformId)).ToArray();
        return Results.Ok(rom.ToContract(status) with
        {
            Formats = content is null ? [] : RomDeliveryPlanner.Plan(content, rom.OriginalFilename, deliveryPolicy,
                MediaKindResolver.Resolve(deliveryPolicy.Kinds, content, platforms), chdAdmission)
                .Select(option => option.ToContract(deliveryPolicy.Formats)).ToArray(),
            Matches = matches.Select(match => new Models.RomMatch
            {
                DatId = new Sqid(match.DatId).ToString(),
                DatName = match.DatName,
                DatGameId = new Sqid(match.DatGameId).ToString(),
                GameName = match.GameName,
                RomName = match.RomName,
                TitleId = match.TitleId is int titleId ? new Sqid(titleId).ToString() : null,
                TitleName = match.TitleName,
                SystemKey = systemKeys.Optional(match.PlatformId),
                PlatformName = match.PlatformName
            }).ToList()
        });
    }

    private static async Task<IResult> Download(
        Sqid romId,
        HttpContext context,
        IRomRepository romRepository,
        IRomContentReader romContent,
        ICurrentUser currentUser,
        IRomContentCatalog contentCatalog,
        [FromServices] IReferenceCatalogService referenceCatalog,
        [FromServices] IRomChdDeliveryAdmission chdAdmission,
        [FromServices] RomDeliveryPolicy deliveryPolicy,
        CancellationToken cancellationToken,
        string? format = null)
    {
        var requested = RomDeliveryFormats.Parse(format ?? "original");
        if (requested.IsError)
            return requested.Errors.ToProblem();
        var rom = await romRepository.GetByIdAsync(romId, cancellationToken);
        if (rom is null)
        {
            return new[] { CatalogErrors.RomNotFound() }.ToProblem();
        }

        int? libraryId = currentUser.HasRole(RomdRoleType.Contributor) ? null : currentUser.LibraryId;
        bool isAccessible = await romRepository.IsAccessibleAsync(rom.Id, libraryId, cancellationToken);
        if (!isAccessible)
        {
            return ProblemResults.Problem(
                StatusCodes.Status403Forbidden,
                "Roms.AccessDenied",
                "This ROM is not accessible from your library.");
        }

        var content = await contentCatalog.FindByRomIdAsync(rom.Id, cancellationToken);
        if (content is null)
            return new[] { CatalogErrors.RomNotFound() }.ToProblem();
        var deliverySystems = await RomDeliverySystems.ResolveAsync(deliveryPolicy, referenceCatalog, cancellationToken);
        var matches = await romRepository.GetMatchesAsync(rom.Id, cancellationToken);
        var platforms = matches.Select(match => deliverySystems.KeyFor(match.PlatformId)).ToArray();
        var target = RomDeliveryPlanner.Plan(content, rom.OriginalFilename, deliveryPolicy,
                MediaKindResolver.Resolve(deliveryPolicy.Kinds, content, platforms), chdAdmission)
            .SingleOrDefault(option => option.Format == requested.Value)?.Target;
        if (target is null)
            return ProblemResults.Problem(StatusCodes.Status409Conflict,
            "Delivery.FormatUnavailable", "The requested delivery format is unavailable.");
        Stream? stream;
        try
        { stream = await romContent.OpenAsync(target, cancellationToken); }
        catch (ResourceCapacityException) { return ContentCapacityResponse.Unavailable(context); }
        if (stream is null)
        {
            return new[] { CatalogErrors.RomNotFound() }.ToProblem();
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentLength = target.SizeBytes;
        return Results.File(
            stream,
            "application/octet-stream",
            target.Filename);
    }

    private static async Task<IResult> Delete(
        Sqid romId,
        ICommandHandler<DeleteRomCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteRomCommand(romId), cancellationToken);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> BatchDelete(
        [FromBody] BatchDeleteRomsRequest request,
        ICommandHandler<BatchDeleteRomsCommand, BatchDeleteResult> handler,
        CancellationToken cancellationToken)
    {
        if (request.RomIds is null ||
            request.RomIds.Count is < BatchDeleteRomsRequest.MinimumRomIds or > BatchDeleteRomsRequest.MaximumRomIds)
        {
            return ProblemResults.ValidationProblem(
                "Roms.InvalidRomIds",
                "romIds",
                $"romIds must contain between {BatchDeleteRomsRequest.MinimumRomIds} and " +
                $"{BatchDeleteRomsRequest.MaximumRomIds} distinct public ROM IDs.");
        }

        if (RequestBodyIds.DecodeAll(request.RomIds, "romIds", "Roms.InvalidRomId", out IReadOnlyList<int> romIds) is { } problem)
            return problem;

        string[] duplicateIds = romIds
            .GroupBy(romId => romId)
            .Where(group => group.Count() > 1)
            .Select(group => IdCoder.Encode(group.Key))
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            return ProblemResults.ValidationProblem(
                "Roms.InvalidRomIds",
                "romIds",
                "Duplicate ROM IDs are not allowed: " +
                string.Join(", ", duplicateIds.Select(id => $"'{id}'")));
        }

        var command = new BatchDeleteRomsCommand { RomIds = romIds };
        var result = await handler.HandleAsync(command, cancellationToken);
        return result.Match(
            response => Results.Ok(new BatchDeleteResponse
            {
                DeletedCount = response.DeletedCount,
                FailedCount = response.FailedCount,
                Errors = response.Errors
            }),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> PurgeUnidentified(
        ICommandHandler<PurgeUnidentifiedRomsCommand, PurgeUnidentifiedRomsResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new PurgeUnidentifiedRomsCommand(), cancellationToken);
        return result.Match(
            response => Results.Ok(new PurgeUnidentifiedResponse
            {
                DeletedCount = response.DeletedCount,
                ReclaimedFileCount = response.ReclaimedFileCount,
            }),
            errors => errors.ToProblem());
    }
}
