using Romd.Application.Common.ReferenceCatalog;
using Romd.Admin.Application.Titles;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Host.Authorization;
using Romd.Domain.Identity;
using Commands = Romd.Contracts.Management.Commands;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Upload endpoints for DATs, ROMs, and mixed archives.
/// </summary>
public static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/upload")
            .WithTags("Upload");

        group.MapGet("batches/{batchId:guid}", async (Guid batchId, IUploadJobRepository repository,
                ICurrentUser user, [FromServices] IReferenceCatalogService referenceCatalog, CancellationToken ct) =>
            {
                if (user.UserId is null)
                    return Results.Unauthorized();
                var jobs = await repository.GetByBatchAsync(batchId,
                    user.HasRole(RomdRoleType.Manager) ? null : user.UserId, ct);
                var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
                return Results.Ok(jobs.Select(j => j.ToContract(systemKeys)).ToList());
            })
            .WithName("GetImportBatch")
            .Produces<List<Models.UploadJobDto>>()
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPost("", Upload)
            .WithName("Upload")
            .WithDescription("Upload any file (DAT, ROM, or archive) for async processing. Supports files up to 10GB.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPost("dat", UploadDat)
            .WithName("UploadDat")
            .WithDescription("Upload a DAT file with validation")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPost("rom", UploadRom)
            .WithName("UploadRom")
            .WithDescription("Upload a ROM file with validation")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Models.UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.RequireContributor);

        group.MapPost("from-path", ImportFromPath)
            .WithName("ImportFromPath")
            .WithDescription(
                "Import files from a server-local directory under Romd:AllowedImportPaths for async processing.")
            .Produces<Models.UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    /// <summary>
    ///     Generic upload — accepts any file, runs full pipeline.
    /// </summary>
    private static async Task<IResult> Upload([FromServices] IReferenceCatalogService referenceCatalog,
        [FromForm] IFormFile? file,
        [FromQuery] string? systemKey,
        [FromQuery] int? maxParallelRoms,
        [FromQuery] bool? allowUnidentified,
        [FromQuery] bool? archiveOnly,
        [FromQuery] bool? trackedOnly,
        ITitleRepository titles,
        [FromQuery] Guid? requestId,
        [FromQuery] Guid? batchId,
        IUploadJobCreator jobCreator,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var validation = ValidateFile(file);
        if (validation is not null)
        {
            return validation;
        }

        if (maxParallelRoms is < 1 or > 32)
            return ProblemResults.ValidationProblem("Upload.InvalidConcurrency", "maxParallelRoms", "Parallel ROM processing must be between 1 and 32.");
        if (requestId == Guid.Empty || batchId == Guid.Empty || requestId.HasValue != batchId.HasValue)
            return ProblemResults.ValidationProblem("Upload.InvalidIdentity", "requestId", "Provide both non-empty request and batch IDs, or neither.");
        if (trackedOnly == true && !await titles.HasTrackedAsync(cancellationToken: ct))
            return NoTrackedTitles();
        await using var stream = file!.OpenReadStream();

        try
        {
            var result = await jobCreator.CreateAsync(
                stream,
                file.FileName,
                new UploadJobOptions
                {
                    RequestId = requestId,
                    BatchId = batchId,
                    PlatformId = (systemKey is null ? (int?)null : await referenceCatalog.RequireSystemAsync(systemKey, ct)),
                    MaxParallelRoms = maxParallelRoms ?? 4,
                    AllowUnidentified = allowUnidentified ?? false,
                    ArchiveOnly = archiveOnly ?? false,
                    TrackedOnly = trackedOnly ?? false,
                    CreatedByUserId = currentUser.UserId
                },
                ct);

            return Results.Accepted(result.StatusUrl, ToContract(result));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    ///     DAT upload — validates content is a DAT before queuing.
    /// </summary>
    private static async Task<IResult> UploadDat([FromServices] IReferenceCatalogService referenceCatalog,
        [FromForm] IFormFile? file,
        [FromQuery] string? systemKey,
        IFileClassifier classifier,
        IUploadJobCreator jobCreator,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var validation = ValidateFile(file);
        if (validation is not null)
        {
            return validation;
        }

        // Validate file is a DAT
        await using var classifyStream = file!.OpenReadStream();
        var classification = await classifier.ClassifyAsync(classifyStream, file.FileName, ct);

        if (classification.Type != FileType.Dat)
        {
            return ProblemResults.ValidationProblem(
                "Upload.InvalidDatFile",
                "file",
                $"File does not appear to be a DAT file. Detected type: {classification.Type}");
        }

        await using var uploadStream = file.OpenReadStream();

        var result = await jobCreator.CreateAsync(
            uploadStream,
            file.FileName,
            new UploadJobOptions { PlatformId = (systemKey is null ? (int?)null : await referenceCatalog.RequireSystemAsync(systemKey, ct)), CreatedByUserId = currentUser.UserId },
            ct);

        return Results.Accepted(result.StatusUrl, ToContract(result));
    }

    /// <summary>
    ///     ROM upload — validates content is not a DAT before queuing.
    /// </summary>
    private static async Task<IResult> UploadRom([FromForm] IFormFile? file,
        [FromQuery] bool? trackedOnly,
        ITitleRepository titles,
        IFileClassifier classifier,
        IUploadJobCreator jobCreator,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var validation = ValidateFile(file);
        if (validation is not null)
        {
            return validation;
        }

        if (trackedOnly == true && !await titles.HasTrackedAsync(cancellationToken: ct))
            return NoTrackedTitles();

        // Validate file is NOT a DAT
        await using var classifyStream = file!.OpenReadStream();
        var classification = await classifier.ClassifyAsync(classifyStream, file.FileName, ct);

        if (classification.Type == FileType.Dat)
        {
            return ProblemResults.ValidationProblem(
                "Upload.InvalidRomFile",
                "file",
                "File appears to be a DAT file. Use POST /upload/dat instead.");
        }

        await using var uploadStream = file.OpenReadStream();

        var result = await jobCreator.CreateAsync(
            uploadStream,
            file.FileName,
            new UploadJobOptions { CreatedByUserId = currentUser.UserId, TrackedOnly = trackedOnly ?? false },
            ct);

        return Results.Accepted(result.StatusUrl, ToContract(result));
    }

    /// <summary>
    ///     Server-side path import — stages allowlisted server-local files into an upload job.
    /// </summary>
    private static async Task<IResult> ImportFromPath([FromServices] IReferenceCatalogService referenceCatalog,
        Commands.ImportFromPath request,
        ITitleRepository titles,
        IPathImportJobCreator jobCreator,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (request.TrackedOnly == true && !await titles.HasTrackedAsync(cancellationToken: ct))
            return NoTrackedTitles();

        int? platformId = request.SystemKey is null ? null : await referenceCatalog.RequireSystemAsync(request.SystemKey, ct);

        var result = await jobCreator.CreateFromPathAsync(
            request.Path,
            new UploadJobOptions
            {
                PlatformId = platformId,
                MaxParallelRoms = request.MaxParallelRoms ?? 4,
                AllowUnidentified = request.AllowUnidentified ?? false,
                ArchiveOnly = request.ArchiveOnly ?? false,
                TrackedOnly = request.TrackedOnly ?? false,
                CreatedByUserId = currentUser.UserId
            },
            request.Move ?? false,
            ct);

        return result.Match(
            created => Results.Accepted(created.StatusUrl, ToContract(created)),
            errors => errors.ToProblem());
    }

    private static IResult NoTrackedTitles() => ProblemResults.ValidationProblem(
        "Upload.NoTrackedTitles", "trackedOnly", "Track at least one title before importing tracked-title ROMs.");

    private static IResult? ValidateFile(IFormFile? file)
    {
        if (file is null || file.Length == 0 || file.Length > 10L * 1024 * 1024 * 1024
            || string.IsNullOrWhiteSpace(file.FileName) || Path.GetFileName(file.FileName.Replace('\\', '/')) is "" or "." or "..")
        {
            return ProblemResults.ValidationProblem(
                "Upload.FileRequired",
                "file",
                "A non-empty file no larger than 10 GB is required.");
        }

        return null;
    }

    private static Models.UploadAccepted ToContract(UploadJobCreationResult result) =>
        new(result.JobId, result.BackgroundJobId, result.StatusUrl);
}
