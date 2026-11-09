using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Admin.Application.Ingestion.Jobs.Commands.RetryFailedItems;
using Romd.Admin.Application.Ingestion.Jobs.Commands.SetJobArchived;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJob;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobArtworkItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobArtworkSummary;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ListJobs;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/jobs").WithTags("Jobs");
        group.MapGet("", List).WithName("ListJobs").Produces<Page<Models.Job>>().ProducesProblem(400)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("{jobId}", Get).WithName("GetJob").Produces<Models.Job>().ProducesProblems(400, 404)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapPatch("{jobId}", Update).WithName("UpdateJob").Produces<Models.Job>().ProducesProblems(400, 404, 409)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("{jobId}/items", Items).WithName("ListJobItems").Produces<Page<Models.JobItem>>().ProducesProblems(400, 404)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapGet("{jobId}/artwork-items", ArtworkItems).WithName("ListJobArtworkItems").Produces<Page<ArtworkCompletionItem>>()
            .ProducesProblems(400, 404).RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapGet("{jobId}/artwork-summary", ArtworkSummary).WithName("GetJobArtworkSummary").Produces<ArtworkCompletionSummary>()
            .ProducesProblems(400, 404).RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapGet("{jobId}/item-export", ExportItems).WithName("GetJobItemExport")
            .WithDescription("Download the complete upload provenance as a JSON array or CSV file; pages are available from items.")
            .Produces(200, contentType: "application/json", additionalContentTypes: ["text/csv"])
            .ProducesProblems(400, 404).RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapPost("{jobId}/cancel", Cancel).WithName("CancelJob").Produces(204).ProducesProblems(400, 404, 409)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);
        group.MapDelete("{jobId}", Delete).WithName("DeleteJob").Produces(204).ProducesProblems(400, 404, 409, 501)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("clear-completed", ClearCompleted).WithName("ClearCompletedJobs").Produces<Models.ClearCompleted>()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        group.MapPost("{jobId}/retry-failed", RetryFailed).WithName("RetryFailedJobItems").Produces<Models.RetryFailedResult>()
            .ProducesProblems(400, 401, 404, 409).RequireAuthorization(AuthorizationPolicies.RequireManager);
        return app;
    }
    private static async Task<IResult> List(IQueryHandler<ListJobsQuery, Page<Models.Job>> handler, CancellationToken ct,
        [StringLength(200)] string? query = null, [ClosedValues(JobFilterKind.Outcome)] string? outcome = null,
        [ClosedValues(JobFilterKind.Type)] string? jobType = null, [ClosedValues(JobFilterKind.Archive)] string archive = "active",
        Guid? correlationId = null, DateTimeOffset? from = null, DateTimeOffset? before = null,
        string? cursor = null, [Range(1, 100)] int limit = 50) =>
        (await handler.HandleAsync(new(query, outcome, jobType, archive, from, before, cursor, limit, correlationId), ct))
            .Match(Results.Ok, errors => errors.ToProblem());
    private static async Task<IResult> Get(Guid jobId, IQueryHandler<GetJobQuery, Models.Job> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(jobId), ct)).Match(Results.Ok, errors => errors.ToProblem());
    private static async Task<IResult> Update(Guid jobId, Models.UpdateJobRequest request,
        ICommandHandler<SetJobArchivedCommand, Models.Job> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(jobId, request.IsArchived), ct)).Match(Results.Ok, errors => errors.ToProblem());
    private static async Task<IResult> Items(Guid jobId, IQueryHandler<GetJobItemsQuery, Page<Models.JobItem>> handler,
        CancellationToken ct, [ClosedValues(JobFilterKind.ItemOutcome)] string? outcome = null,
        [StringLength(200)] string? query = null, string? cursor = null, [Range(1, 100)] int limit = 100) =>
        (await handler.HandleAsync(new(jobId, outcome, limit, cursor, query), ct)).Match(Results.Ok, errors => errors.ToProblem());
    private static async Task<IResult> ArtworkItems(Guid jobId, IQueryHandler<GetJobArtworkItemsQuery, Page<ArtworkCompletionItem>> handler,
        CancellationToken ct, [ClosedValues(JobFilterKind.ArtworkStatus)] string? status = null,
        string? cursor = null, [Range(1, 100)] int limit = 50) =>
        (await handler.HandleAsync(new(jobId, status, cursor, limit), ct)).Match(Results.Ok, errors => errors.ToProblem());
    private static async Task<IResult> ArtworkSummary(Guid jobId, IQueryHandler<GetJobArtworkSummaryQuery, ArtworkCompletionSummary> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(jobId), ct)).Match(Results.Ok, errors => errors.ToProblem());
    private static async Task<IResult> ExportItems(Guid jobId, [FromQuery, Required, ClosedValues(JobFilterKind.ExportFormat)] string format,
        IJobRepository jobs, ICurrentUser user, IReferenceCatalogService referenceCatalog,
        IQueryHandler<ExportJobItemsQuery, JobItemExport> handler, CancellationToken ct)
    {
        var job = await jobs.GetVisibleAsync(jobId, user, ct);
        if (job is null)
            return Missing(jobId);
        if (job is not UploadJob)
            return new[] { JobErrors.FeedUnavailable() }.ToProblem();
        var result = await handler.HandleAsync(new(jobId), ct);
        if (result.IsError)
            return result.Errors.ToProblem();
        try
        {
            return new JobItemExportResult(jobId, format, result.Value, await referenceCatalog.GetSystemKeysAsync(ct));
        }
        catch (Exception primary)
        {
            await result.Value.DisposePreservingFailureAsync(primary);
            throw;
        }
    }
    private static IResult Missing(Guid jobId) => new[] { JobErrors.NotFound(jobId) }.ToProblem();

    private static async Task<IResult> Cancel(Guid jobId,
        ICommandHandler<CancelJobCommand, ErrorOr.Success> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new(jobId), ct)).Match(_ => Results.NoContent(), errors => errors.ToProblem());

    private static async Task<IResult> Delete(
        Guid jobId,
        IJobRepository jobRepo,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var job = await jobRepo.GetVisibleAsync(jobId, currentUser, ct);
        if (job is null)
        {
            return Missing(jobId);
        }

        if (!job.IsArchived)
        {
            return ProblemResults.Problem(
                StatusCodes.Status409Conflict,
                "Jobs.NotArchived",
                "Only archived jobs can be permanently deleted.");
        }

        // Note: For now we don't have a delete method on the repo
        // This would need to be added if permanent deletion is desired
        return ProblemResults.Problem(
            StatusCodes.Status501NotImplemented,
            "Jobs.DeleteNotImplemented",
            "Permanent deletion is not yet implemented.");
    }

    private static async Task<IResult> ClearCompleted(
        IJobRepository jobRepo,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        int archivedCount = await jobRepo.ArchiveCompletedVisibleAsync(JobVisibility.ScopeFor(currentUser), ct);
        return Results.Ok(new Models.ClearCompleted(archivedCount));
    }

    private static async Task<IResult> RetryFailed(
        Guid jobId,
        IJobRepository jobRepo,
        ICurrentUser currentUser,
        ICommandHandler<RetryFailedItemsCommand, Models.RetryFailedResult> handler,
        CancellationToken ct)
    {
        var job = await jobRepo.GetVisibleAsync(jobId, currentUser, ct);
        if (job is null)
            return Missing(jobId);
        if (job is MediaReconciliationJob)
            return ProblemResults.Problem(StatusCodes.Status409Conflict, "MediaReconciliation.ExactRetryRequired",
                "Retry this media job with an explicit request identity.");
        var result = await handler.HandleAsync(new RetryFailedItemsCommand(jobId), ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }
}
