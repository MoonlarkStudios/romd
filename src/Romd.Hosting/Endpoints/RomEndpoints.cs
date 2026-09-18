using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Dashboard.Queries.GetLibrarySummary;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Source.Rom.Commands.PurgeUnidentified;
using Romd.Admin.Application.Storage.Files;
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
    public static IEndpointRouteBuilder MapRomEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/roms")
            .WithTags("Roms");

        group.MapGet("", List)
            .WithName("ListRoms")
            .Produces<CommonModels.Page<Models.Rom>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("stats", GetStats)
            .WithName("GetLibraryStats")
            .Produces<Models.LibraryStats>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("library-summary", GetLibrarySummary)
            .WithName("GetLibrarySummary")
            .Produces<Models.LibrarySummary>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{romId}", GetById)
            .WithName("GetRomById")
            .Produces<Models.Rom>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{romId}/download", Download)
            .WithName("DownloadRom")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapDelete("{romId}", Delete)
            .WithName("DeleteRom")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        // Batch operations
        group.MapDelete("batch", BatchDelete)
            .WithName("BatchDeleteRoms")
            .Produces<BatchDeleteResponse>()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapDelete("unidentified", PurgeUnidentified)
            .WithName("PurgeUnidentifiedRoms")
            .WithDescription("Delete every unidentified ROM (the inbox) in one operation.")
            .Produces<PurgeUnidentifiedResponse>()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<IResult> List(
        IRomRepository romRepository,
        string? status = null,
        string? cursor = null,
        int limit = 50,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (search?.Length > 200)
            return Results.BadRequest("Filename search must be 200 characters or fewer.");
        RomCatalogStatus? statusFilter = status?.ToLowerInvariant() switch
        {
            "cataloged" => RomCatalogStatus.Cataloged,
            "unrouted" => RomCatalogStatus.Unrouted,
            "unidentified" => RomCatalogStatus.Unidentified,
            _ => null
        };

        var result = await romRepository.ListAsync(
            statusFilter,
            cursor,
            limit,
            cancellationToken,
            search);

        var page = new CommonModels.Page<Models.Rom>
        {
            Items = result.Items.Select(x => x.Rom.ToContract(x.Status)).ToList(),
            NextCursor = result.NextCursor,
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

    private static async Task<IResult> GetLibrarySummary(
        IQueryHandler<GetLibrarySummaryQuery, Models.LibrarySummary> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetLibrarySummaryQuery(), cancellationToken);
        return result.Match(
            summary => Results.Ok(summary),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> GetById([FromServices] IReferenceCatalogService referenceCatalog,
        Sqid romId,
        IRomRepository romRepository,
        CancellationToken cancellationToken)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(cancellationToken);
        var rom = await romRepository.GetByIdAsync(romId, cancellationToken);
        if (rom is null)
        {
            return Results.NotFound();
        }

        var status = await romRepository.GetStatusAsync(rom.Id, cancellationToken);
        var matches = await romRepository.GetMatchesAsync(rom.Id, cancellationToken);
        return Results.Ok(rom.ToContract(status) with
        {
            Matches = matches.Select(match => new Models.RomMatch
            {
                DatId = new Sqid(match.DatId).ToString(), DatName = match.DatName,
                DatGameId = new Sqid(match.DatGameId).ToString(), GameName = match.GameName,
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
        IRomRepository romRepository,
        IFileStorageService fileStorage,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var rom = await romRepository.GetByIdAsync(romId, cancellationToken);
        if (rom is null)
        {
            return Results.NotFound();
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

        var stream = await fileStorage.RetrieveByIdAsync(rom.FileId, cancellationToken);
        if (stream is null)
        {
            return Results.NotFound();
        }

        return Results.File(
            stream,
            "application/octet-stream",
            rom.OriginalFilename);
    }

    private static async Task<IResult> Delete(
        Sqid romId,
        ICommandHandler<DeleteRomCommand, Deleted> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteRomCommand(romId), cancellationToken);
        return result.Match(
            _ => Results.NoContent(),
            errors => errors.Count > 0 && errors[0].Type == ErrorType.NotFound
                ? Results.NotFound()
                : errors.ToProblem());
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

        var romIds = new List<int>(request.RomIds.Count);
        var invalidIds = new List<string>();
        var seenRomIds = new HashSet<int>();
        var duplicateIds = new List<string>();
        foreach (string? publicId in request.RomIds)
        {
            if (IdCoder.TryDecode(publicId, out int romId))
            {
                romIds.Add(romId);
                if (!seenRomIds.Add(romId))
                    duplicateIds.Add(publicId);
            }
            else
                invalidIds.Add(publicId ?? "<null>");
        }

        if (invalidIds.Count > 0)
        {
            return ProblemResults.ValidationProblem(
                "Roms.InvalidRomId",
                "romIds",
                $"Invalid ROM ID{(invalidIds.Count == 1 ? string.Empty : "s")}: " +
                string.Join(", ", invalidIds.Select(id => $"'{id}'")));
        }

        if (duplicateIds.Count > 0)
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
                DeletedCount = response.DeletedCount, FailedCount = response.FailedCount, Errors = response.Errors
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
