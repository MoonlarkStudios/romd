using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.Admin.Application.Export.Queries.AuthorizeExportDownload;
using Romd.Admin.Application.Export.Queries.PrepareReleaseDownload;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Export;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/export")
            .WithTags("Export");

        // One collection of download tickets: the body names the title, release or file a ticket downloads, and
        // the created member's address is its anonymous, capability-authenticated redemption
        // (docs/decisions/api-contract-policy.md, "Resource Model" and "Protected By Default").
        var tickets = app.MapGroup("/download-tickets")
            .WithTags("Export");

        tickets.MapPost("", CreateDownloadTicket)
            .WithName("CreateDownloadTicket")
            .WithDescription("Issue a ticket that downloads one title or release as a zip archive, one complete file, or a completed export archive")
            .ProducesCreated<AdminDownloadTicket>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden,
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        tickets.MapGet("{token}", RedeemDownload)
            .WithName("RedeemAdminDownload")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .AllowAnonymous();

        group.MapPost("library", ExportLibrary)
            .WithName("ExportLibrary")
            .WithDescription("Start a library export background job")
            .ProducesAcceptedJob()
            .ProducesProblems(StatusCodes.Status400BadRequest)
            .ProducesProblems(StatusCodes.Status403Forbidden)
            .ProducesProblems(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        return app;
    }

    /// <summary>
    ///     Validates the id and format in one stage, each failure keyed by its field, then issues the ticket for the
    ///     named resource; the target is a body enum, which binding validates. An unresolved resource is 404.
    /// </summary>
    private static async Task<IResult> CreateDownloadTicket(CreateDownloadTicketRequest request, HttpContext context,
        IExportRepository exportRepository, IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
        IRomContentCatalog contentCatalog, IFileStorageService storage, IAdminDownloadTickets tickets,
        IQueryHandler<PrepareReleaseDownloadQuery, AdminDownloadTicket> releases, IRomRepository roms, ICurrentUser user,
        IExportAuthorizationReader authorization, [FromServices] IReferenceCatalogService referenceCatalog,
        [FromServices] IRomChdDeliveryAdmission chdAdmission,
        [FromServices] RomDeliveryPolicy deliveryPolicy,
        IQueryHandler<AuthorizeExportDownloadQuery, ExportJob> exports, CancellationToken ct)
    {
        var failures = new Dictionary<string, string[]>();
        if (request.Target == DownloadTicketTarget.ExportJob)
        {
            if (!Guid.TryParseExact(request.Id, "D", out var jobId) || jobId == Guid.Empty || jobId.ToString("D") != request.Id)
                failures["id"] = ["Use a canonical nonempty export job ID."];
            if (request.Format is not null)
                failures["format"] = ["Completed exports do not accept a format override."];
            if (failures.Count > 0)
                return ProblemResults.ValidationProblem("Export.InvalidDownloadTicket", failures);
            return await PrepareExportJobDownload(jobId, context, exports, tickets, ct);
        }
        int id = RequestBodyIds.Collect([request.Id], "id", failures)[0];
        var format = RomDeliveryFormats.Parse(request.Format ?? "original");
        if (format.IsError)
            failures["format"] = ["The format is one of original, rvz, chd."];
        if (failures.Count > 0)
            return ProblemResults.ValidationProblem("Export.InvalidDownloadTicket", failures);
        return request.Target switch
        {
            DownloadTicketTarget.Title => await PrepareTitleDownload(id, format.Value, context, exportRepository,
                scopeHandler, contentCatalog, storage, tickets, referenceCatalog, chdAdmission, deliveryPolicy, ct),
            DownloadTicketTarget.Release => await PrepareReleaseDownload(id, format.Value, context, releases, ct),
            DownloadTicketTarget.Rom => await PrepareFileDownload(id, format.Value, context, roms, user, tickets,
                storage, authorization, contentCatalog, referenceCatalog, chdAdmission, deliveryPolicy, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Target, "Not a download ticket target.")
        };
    }

    /// <summary>
    ///     A ticket is created at its redemption URL. The URL is a capability: it travels only in this response, and
    ///     request logs redact it on redemption (docs/decisions/api-contract-policy.md, "Protected By Default").
    /// </summary>
    private static IResult TicketCreated(HttpContext context, AdminDownloadTicket ticket)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Created(ticket.DownloadUrl, ticket);
    }

    private static async Task<ErrorOr<AdminDownloadPlan>> PlanTitleAsync(int titleId,
        IExportRepository exportRepository, IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
        IRomContentCatalog contentCatalog, IReferenceCatalogService referenceCatalog, IRomChdDeliveryAdmission chdAdmission,
        RomDeliveryPolicy deliveryPolicy, RomDeliveryFormat format, CancellationToken ct)
    {
        var archive = await TitleArchives.ReadAsync(titleId, scopeHandler, exportRepository, referenceCatalog, deliveryPolicy, ct);
        if (archive.IsError)
            return archive.Errors;
        var bound = await ExportArchivePlanner.BindAsync(archive.Value.Entries, format, contentCatalog, deliveryPolicy, ct: ct,
            chdAdmission: chdAdmission);
        if (bound.IsError)
            return bound.Errors;
        return new AdminDownloadPlan($"{ExportArchivePaths.SafeFilename(archive.Value.TitleName)}.zip", bound.Value.Entries, true);
    }

    private static async Task<IResult> PrepareTitleDownload(int titleId, RomDeliveryFormat format, HttpContext context,
        IExportRepository exportRepository, IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope> scopeHandler,
        IRomContentCatalog contentCatalog, IFileStorageService storage, IAdminDownloadTickets tickets,
        IReferenceCatalogService referenceCatalog, IRomChdDeliveryAdmission chdAdmission, RomDeliveryPolicy deliveryPolicy,
        CancellationToken ct)
    {
        var plan = await PlanTitleAsync(titleId, exportRepository, scopeHandler, contentCatalog, referenceCatalog, chdAdmission,
            deliveryPolicy, format, ct);
        if (plan.IsError)
            return plan.Errors.ToProblem();
        foreach (var entry in plan.Value.Entries)
            if (!await storage.ExistsAsync(entry.FileId, ct))
                return new[] { ExportErrors.FileUnavailable() }.ToProblem();
        return TicketCreated(context, tickets.Issue(plan.Value));
    }

    private static async Task<IResult> PrepareReleaseDownload(int releaseId, RomDeliveryFormat format,
        HttpContext context, IQueryHandler<PrepareReleaseDownloadQuery, AdminDownloadTicket> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(releaseId, format), ct))
            .Match(ticket => TicketCreated(context, ticket), errors => errors.ToProblem());

    private static async Task<IResult> PrepareFileDownload(int romId, RomDeliveryFormat format, HttpContext context,
        IRomRepository roms, ICurrentUser user, IAdminDownloadTickets tickets,
        IFileStorageService storage, IExportAuthorizationReader authorization, IRomContentCatalog contentCatalog,
        IReferenceCatalogService referenceCatalog, IRomChdDeliveryAdmission chdAdmission, RomDeliveryPolicy deliveryPolicy,
        CancellationToken ct)
    {
        var file = await roms.GetByIdAsync(romId, ct);
        if (file is null)
            return new[] { ExportErrors.FileNotFound() }.ToProblem();
        var unrestricted = user.HasRole(RomdRoleType.Contributor);
        var library = !unrestricted && user.UserId is { } userId
            ? await authorization.GetEffectiveLibraryScopeAsync(userId, ct) : null;
        if (user.UserId is null || (!unrestricted && library is null) ||
            !await roms.IsAccessibleAsync(file.Id, library?.LibraryId, ct))
            return new[] { ExportErrors.AccessDenied() }.ToProblem();
        var content = await contentCatalog.FindByRomIdAsync(file.Id, ct);
        if (content is null)
            return new[] { ExportErrors.FileNotFound() }.ToProblem();
        var deliverySystems = await RomDeliverySystems.ResolveAsync(deliveryPolicy, referenceCatalog, ct);
        var matches = await roms.GetMatchesAsync(file.Id, ct);
        string?[] platforms = matches.Select(match => deliverySystems.KeyFor(match.PlatformId)).ToArray();
        var target = RomDeliveryPlanner.Plan(content, ExportArchivePaths.SafeFilename(file.OriginalFilename), deliveryPolicy,
                MediaKindResolver.Resolve(deliveryPolicy.Kinds, content, platforms), chdAdmission)
            .SingleOrDefault(option => option.Format == format)?.Target;
        if (target is null)
            return ProblemResults.Problem(StatusCodes.Status409Conflict,
            "Delivery.FormatUnavailable", "The requested delivery format is unavailable.");
        if (!await storage.ExistsAsync(target.FileId, ct))
            return new[] { ExportErrors.FileNotFound() }.ToProblem();
        return TicketCreated(context, tickets.Issue(new(target.Filename,
            [new(target.FileId, target.Filename, file.Id, target, platforms)], IsArchive: false)));
    }

    private static async Task<IResult> RedeemDownload(string token, HttpContext context,
        IAdminDownloadTickets tickets, IExportArchiveWriter writer, IFileStorageService storage, IRomContentReader romContent,
        CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var plan = tickets.Resolve(token);
        if (plan is null)
            return DownloadUnavailable();
        if (plan.CompletedExportPath is { } completed)
        {
            if (!plan.IsArchive || plan.Entries.Count != 0 || !Path.IsPathFullyQualified(completed))
                return DownloadUnavailable();
            try
            {
                var artifact = new FileStream(completed, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return Results.File(artifact, "application/zip", plan.Filename);
            }
            catch (FileNotFoundException) { return DownloadUnavailable(); }
            catch (DirectoryNotFoundException) { return DownloadUnavailable(); }
        }
        if (!plan.IsArchive)
        {
            var entry = plan.Entries[0];
            Stream? file;
            try
            {
                file = entry.Target is { } target
                    ? await romContent.OpenAsync(target, ct)
                    : entry.RomId is { } romId
                    ? await romContent.OpenVerifiedByRomIdAsync(romId, ct)
                    : await storage.RetrieveByIdAsync(entry.FileId, ct);
            }
            catch (ResourceCapacityException) { return ContentCapacityResponse.Unavailable(context); }
            if (file is null)
                return DownloadUnavailable();
            if (entry.Target is { } bound)
                context.Response.ContentLength = bound.SizeBytes;
            return Results.File(file, "application/octet-stream", plan.Filename);
        }
        foreach (var entry in plan.Entries)
            if (entry.Target is null && !await storage.ExistsAsync(entry.FileId, ct))
                return DownloadUnavailable();
        return Results.Stream(async stream =>
        {
            try
            { await writer.WriteAsync(stream, plan.Entries, ct: ct); }
            catch { context.Abort(); throw; }
        }, "application/zip", plan.Filename);
    }

    private static IResult DownloadUnavailable() =>
        ProblemResults.Problem(StatusCodes.Status404NotFound, ProblemCodes.DeliveryContentNotFound,
            "The download is unavailable.");

    private static async Task<IResult> ExportLibrary(
        ExportLibraryRequest? request,
        ICommandHandler<QueueLibraryExportCommand, Guid> handler,
        CancellationToken ct)
    {
        var requested = RomDeliveryFormats.Parse(request?.Format ?? "original");
        if (requested.IsError)
            return requested.Errors.ToProblem();
        var identityProblem = RequestBodyIds.DecodeOptional(request?.LibraryId, "libraryId", "Export.InvalidLibraryId", out int? requestedLibraryId);
        if (identityProblem is not null)
            return identityProblem;

        var jobId = await handler.HandleAsync(new QueueLibraryExportCommand(requestedLibraryId, requested.Value), ct);

        return jobId.ToAccepted();
    }

    private static async Task<IResult> PrepareExportJobDownload(Guid jobId, HttpContext context,
        IQueryHandler<AuthorizeExportDownloadQuery, ExportJob> handler, IAdminDownloadTickets tickets, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AuthorizeExportDownloadQuery(jobId), ct);
        if (result.IsError)
            return result.Errors.ToProblem();
        if (result.Value.ExportPath is not { } path || !File.Exists(path))
            return new[] { ExportErrors.NotFound() }.ToProblem();
        path = Path.GetFullPath(path);
        context.Response.Headers.CacheControl = "private, no-store";
        return TicketCreated(context, tickets.Issue(new("romd-export.zip", [], true, path)));
    }
}
