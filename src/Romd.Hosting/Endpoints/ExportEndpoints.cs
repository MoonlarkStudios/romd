using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.AuthorizeExportDownload;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;
using Romd.Host.Authorization;
using System.IO.Compression;

namespace Romd.Host.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/export")
            .WithTags("Export");

        group.MapPost("title/{titleId}", ExportTitle)
            .WithName("ExportTitle")
            .WithDescription("Export a single title as a zip archive")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("library", ExportLibrary)
            .WithName("ExportLibrary")
            .WithDescription("Start a library export background job")
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{jobId:guid}/download", DownloadExport)
            .WithName("DownloadExport")
            .WithDescription("Download a completed export archive")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    private static async Task<IResult> ExportTitle(
        Sqid titleId,
        IExportRepository exportRepository,
        IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
        IFileStorageService fileStorage,
        CancellationToken ct)
    {
        var scopeResult = await scopeHandler.HandleAsync(new ResolveExportScopeQuery(null), ct);
        if (scopeResult.IsError)
            return scopeResult.Errors.ToProblem();

        var files = await exportRepository.GetExportFilesForTitleAsync(
            titleId,
            scopeResult.Value,
            ct);
        if (files.Count == 0)
            return Results.NotFound();

        var titleName = files[0].TitleName;

        return Results.Stream(async stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var fileStream = await fileStorage.RetrieveByIdAsync(file.FileId, ct);
                if (fileStream is null)
                    continue;

                await using (fileStream)
                {
                    var entry = archive.CreateEntry(
                        SanitizePath(file.OriginalFilename),
                        CompressionLevel.NoCompression);
                    await using var entryStream = entry.Open();
                    await fileStream.CopyToAsync(entryStream, ct);
                }
            }
        }, "application/zip", $"{SanitizePath(titleName)}.zip");
    }

    private static async Task<IResult> ExportLibrary(
        ExportLibraryRequest? request,
        IExportScheduler exportScheduler,
        ICurrentUser currentUser,
        IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
        CancellationToken ct)
    {
        int? requestedLibraryId = null;

        if (request?.LibraryId is not null)
        {
            if (!IdCoder.TryDecode(request.LibraryId, out var decodedLibraryId))
            {
                return ProblemResults.ValidationProblem(
                    "Export.InvalidLibraryId",
                    "libraryId",
                    $"'{request.LibraryId}' is not a valid library ID.");
            }

            requestedLibraryId = decodedLibraryId;
        }

        var scopeResult = await scopeHandler.HandleAsync(
            new ResolveExportScopeQuery(requestedLibraryId),
            ct);
        if (scopeResult.IsError)
            return scopeResult.Errors.ToProblem();

        var jobId = await exportScheduler.EnqueueLibraryExportAsync(
            scopeResult.Value.Scope,
            currentUser.UserId,
            ct);

        if (jobId == Guid.Empty)
        {
            return ProblemResults.Problem(
                StatusCodes.Status409Conflict,
                "Export.AlreadyInProgress",
                "An export is already in progress.");
        }

        return Results.Accepted(value: new { JobId = jobId });
    }

    private static async Task<IResult> DownloadExport(
        Guid jobId,
        IQueryHandler<AuthorizeExportDownloadQuery, ExportJob> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AuthorizeExportDownloadQuery(jobId), ct);
        if (result.IsError)
            return result.Errors.ToProblem();

        var job = result.Value;
        string exportPath = job.ExportPath!;

        if (!File.Exists(exportPath))
            return Results.NotFound();

        var stream = new FileStream(exportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Results.File(stream, "application/zip", "romd-export.zip");
    }

    private static string SanitizePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}

public sealed record ExportLibraryRequest
{
    public string? LibraryId { get; init; }
}
