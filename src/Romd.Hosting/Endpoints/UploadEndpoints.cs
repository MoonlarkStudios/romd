using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Ingestion.Commands.AcceptUpload;
using Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Host.Authorization;
using Romd.Hosting;
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
            request.AllowUnidentified ?? false, request.ArchiveOnly ?? false, request.TrackedOnly ?? false, request.Move ?? false), ct);

        return result.ToAccepted();
    }

}
