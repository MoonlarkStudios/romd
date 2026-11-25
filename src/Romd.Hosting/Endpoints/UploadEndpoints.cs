using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Ingestion.Commands.AcceptUpload;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Admin.Application.Ingestion.Import.Commands.StartServerImport;
using Romd.Admin.Application.Ingestion.Import.Queries.GetServerImport;
using Romd.Admin.Application.Ingestion.Import.Queries.GetServerImportRetry;
using Romd.Admin.Application.Ingestion.Import.Queries.ListImportFolders;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Host.Authorization;
using Romd.Hosting;
using Romd.Contracts.Management.Models;
using Commands = Romd.Contracts.Management.Commands;

namespace Romd.Host.Endpoints;

/// <summary>
///     Upload endpoints for DATs, ROMs, and mixed archives.
/// </summary>
public static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        // The configured maximum applies to file bytes; reserve bounded room for multipart framing.
        long maxUploadBytes = RomdHostRegistrationExtensions.GetMaxUploadBytes(
            app.ServiceProvider.GetRequiredService<IRomdOptions>());
        long maxRequestBytes = maxUploadBytes > long.MaxValue - 2 * 1024 * 1024
            ? long.MaxValue
            : maxUploadBytes + 2 * 1024 * 1024;
        var group = app.MapGroup("/upload")
            .WithTags("Upload");

        group.MapPost("", Upload)
            .WithName("Upload")
            .WithDescription("Upload any file (DAT, ROM, or archive) for async processing, up to the configured maximum upload size.")
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict)
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxRequestBytes })
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPost("dat", UploadDat)
            .WithName("UploadDat")
            .WithDescription("Upload a DAT file with validation")
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict)
            .WithMetadata(new RequestSizeLimitAttribute(34 * 1024 * 1024))
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPost("rom", UploadRom)
            .WithName("UploadRom")
            .WithDescription("Upload a ROM file with validation")
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict)
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxRequestBytes })
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPost("from-path", ImportFromPath)
            .WithName("ImportFromPath")
            .WithDescription(
                "Import files from a server-local directory under Romd:AllowedImportPaths for async processing.")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("from-path/folders", ListImportFolders)
            .WithName("ListImportFolders")
            .WithDescription(
                "List the allowed import roots, or the folders directly inside one, for choosing a path import source.")
            .Produces<ImportFolderListing>()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("server-folder", GetServerImport)
            .WithName("GetServerImport")
            .WithDescription("Read the configured server import folder and its last check.")
            .Produces<ServerImportStatus>()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapGet("server-folder/retries/{jobId}", GetServerImportRetry)
            .WithName("GetServerImportRetry")
            .WithDescription("Review the unchanged, changed, missing and unavailable originals from a retained server folder import.")
            .Produces<ServerImportRetryReview>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("server-folder", StartServerImport)
            .WithName("StartServerImport")
            .WithDescription("Import ready files from the configured server folder or retry a reviewed batch. A changed review returns 409.")
            .Produces<ServerImportStatus>()
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    /// <summary>
    ///     Generic upload — accepts any file, runs full pipeline.
    /// </summary>
    private static async Task<IResult> Upload(
        [FromForm] IFormFile? file,
        SystemKeyParameter? systemKey,
        [FromQuery] int? maxParallelRoms,
        [FromQuery] bool? allowUnidentified,
        [FromQuery] bool? archiveOnly,
        [FromQuery] bool? trackedOnly,
        [FromQuery] Guid? requestId,
        [FromQuery] Guid? batchId,
        ICommandHandler<AcceptUploadCommand, Guid> handler,
        CancellationToken ct) =>
        (await handler.HandleAsync(new(UploadKind.Generic, Input(file), systemKey?.SystemId,
            maxParallelRoms, allowUnidentified, archiveOnly, trackedOnly, requestId, batchId), ct)).ToAccepted();

    /// <summary>DAT upload validates its own classification before accepting a job.</summary>
    private static async Task<IResult> UploadDat(
        [FromForm] IFormFile? file,
        SystemKeyParameter? systemKey,
        ICommandHandler<AcceptUploadCommand, Guid> handler,
        CancellationToken ct) =>
        (await handler.HandleAsync(new(UploadKind.Dat, Input(file), systemKey?.SystemId), ct)).ToAccepted();

    /// <summary>ROM upload rejects DAT input and preserves all other classifications.</summary>
    private static async Task<IResult> UploadRom([FromForm] IFormFile? file,
        [FromQuery] bool? trackedOnly,
        ICommandHandler<AcceptUploadCommand, Guid> handler,
        CancellationToken ct) =>
        (await handler.HandleAsync(new(UploadKind.Rom, Input(file), TrackedOnly: trackedOnly), ct)).ToAccepted();

    private static IUploadInput? Input(IFormFile? file) => file is null ? null : new MultipartInput(file);

    private sealed class MultipartInput(IFormFile file) : IUploadInput
    {
        public string FileName { get; } = file.FileName;
        public long Length { get; } = file.Length;
        public Stream OpenRead() => file.OpenReadStream();
    }

    /// <summary>
    ///     Server-side path import — stages allowlisted server-local files into an upload job.
    /// </summary>
    private static async Task<IResult> ImportFromPath(Commands.ImportFromPath request,
        ICommandHandler<ImportFromPathCommand, Guid> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.Path, request.SystemKey, request.MaxParallelRoms ?? 4,
            request.AllowUnidentified ?? false, request.ArchiveOnly ?? false, request.TrackedOnly ?? false,
            request.Move ?? false, request.RequestId), ct);

        return result.ToAccepted();
    }

    private static async Task<IResult> ListImportFolders(IQueryHandler<ListImportFoldersQuery, ImportFolderListing> handler,
        CancellationToken ct, [FromQuery, StringLength(4096)] string? path = null) =>
        (await handler.HandleAsync(new(path), ct)).Match(Results.Ok, errors => errors.ToProblem());

    private static async Task<IResult> GetServerImport(
        [FromServices] IQueryHandler<GetServerImportQuery, ServerImportView> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(), ct)).Match(status => Results.Ok(ToContract(status)), errors => errors.ToProblem());

    private static async Task<IResult> GetServerImportRetry(Guid jobId,
        [FromServices] IQueryHandler<GetServerImportRetryQuery, ServerImportRetryReview> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(jobId), ct)).Match(Results.Ok, errors => errors.ToProblem());

    private static async Task<IResult> StartServerImport(Commands.StartServerImport request,
        [FromServices] ICommandHandler<StartServerImportCommand, ServerImportResult> handler,
        [FromServices] ICurrentUser user, CancellationToken ct) =>
        (await handler.HandleAsync(new(user.UserId, request.RetryJobId, request.ReviewedRevision), ct)).Match(
            result => result.AcceptedJobId is { } jobId ? JobAccepted.Result(jobId) : Results.Ok(ToContract(result.Status)),
            errors => errors.ToProblem());

    private static ServerImportStatus ToContract(ServerImportView status) =>
        new(status.Directory, status.IntervalMinutes, status.NextCheckAt, status.Activity?.CheckedAt,
            status.Activity?.Outcome.ToString(), status.Activity?.WaitingFiles ?? 0, status.Activity?.Message,
            status.Job?.Id, status.Job?.Phase.ToString(), status.Activity?.LastQueuedFiles ?? 0,
            status.Job is { IsTerminal: false }, status.Job?.CanRetry == true);
}
