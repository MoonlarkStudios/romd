using Romd.Application.Common.ReferenceCatalog;
using System.Text;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobHistory;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs.Commands.RetryFailedItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Host.Authorization;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoints for managing and monitoring jobs.
/// </summary>
public static class JobEndpoints
{
    /// <summary>
    ///     Maps job endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/jobs")
            .WithTags("Jobs");

        group.MapGet("", GetRecent)
            .WithName("GetRecentJobs")
            .Produces<IReadOnlyList<Models.JobDto>>()
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("history", GetHistory)
            .WithName("GetJobHistory")
            .Produces<Models.JobHistoryPage>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{id:guid}", GetById)
            .WithName("GetJobById")
            .Produces<Models.JobDto>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{id:guid}/items", GetItems)
            .WithName("GetJobItems")
            .Produces<Models.JobItemPage>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapGet("{id:guid}/items/export", ExportItems)
            .WithName("ExportJobItems")
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("{id:guid}/archive", Archive)
            .WithName("ArchiveJob")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("{id:guid}/cancel", Cancel)
            .WithName("CancelJob")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapDelete("{id:guid}", Delete)
            .WithName("DeleteJob")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("clear-completed", ClearCompleted)
            .WithName("ClearCompletedJobs")
            .Produces<Models.ClearCompleted>()
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        group.MapPost("{id:guid}/retry-failed", RetryFailed)
            .WithName("RetryFailedJobItems")
            .Produces<Models.RetryFailedResult>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .RequireAuthorization(AuthorizationPolicies.RequireManager);

        return app;
    }

    private static async Task<IResult> GetHistory(
        IQueryHandler<GetJobHistoryQuery, Models.JobHistoryPage> handler,
        CancellationToken ct,
        string? search = null, string? outcome = null, string? jobType = null,
        string archive = "active", DateTimeOffset? from = null, DateTimeOffset? before = null,
        string? cursor = null, int limit = 50)
    {
        var result = await handler.HandleAsync(new GetJobHistoryQuery(search, outcome, jobType, archive, from, before, cursor, limit), ct);
        return result.Match(page => Results.Ok(page), errors => errors.ToProblem());
    }

    private static async Task<IResult> GetRecent([FromServices] IReferenceCatalogService referenceCatalog,
        [FromQuery] int? limit,
        [FromQuery] bool? includeArchived,
        IJobRepository jobRepo,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        int resolvedLimit = limit ?? 50;
        bool resolvedIncludeArchived = includeArchived ?? false;

        IReadOnlyList<Job> jobs = currentUser.HasRole(RomdRoleType.Manager)
            ? await jobRepo.GetRecentAsync(resolvedLimit, resolvedIncludeArchived, ct)
            : currentUser.UserId is { } userId
                ? await jobRepo.GetRecentForUserAsync(userId, resolvedLimit, resolvedIncludeArchived, ct)
                : [];

        return Results.Ok(jobs.Select(j => j.ToContract(systemKeys)).ToList());
    }

    private static async Task<IResult> GetById([FromServices] IReferenceCatalogService referenceCatalog,
        Guid id,
        IJobRepository jobRepo,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var job = await GetAuthorizedJobAsync(id, jobRepo, currentUser, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(job.ToContract(systemKeys));
    }

    private static async Task<IResult> GetItems([FromServices] IReferenceCatalogService referenceCatalog,
        Guid id,
        [FromQuery] string? outcome,
        [FromQuery] int? limit,
        [FromQuery] Guid? cursor,
        [FromQuery] string? search,
        IJobRepository jobRepo,
        IQueryHandler<GetJobItemsQuery, JobItemPageResult> handler,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var job = await GetAuthorizedJobAsync(id, jobRepo, currentUser, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (search?.Length > 200)
            return ProblemResults.ValidationProblem("Import.SearchTooLong", "search", "Search must be 200 characters or fewer.");
        int resolvedLimit = Math.Clamp(limit ?? 100, 1, 2000);
        var result = await handler.HandleAsync(
            new GetJobItemsQuery(id, JobItemMapping.ParseOutcome(outcome), resolvedLimit, cursor, search),
            ct);
        var page = result.Value;

        return Results.Ok(new Models.JobItemPage
        {
            Items = page.Items.Select(i => i.ToContract(systemKeys)).ToList(),
            HasMore = page.HasMore,
            NextCursor = page.NextCursor
        });
    }

    private static async Task<IResult> ExportItems([FromServices] IReferenceCatalogService referenceCatalog,
        Guid id,
        [FromQuery] string? format,
        IJobRepository jobRepo,
        IQueryHandler<ExportJobItemsQuery, IReadOnlyList<JobItemView>> handler,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var job = await GetAuthorizedJobAsync(id, jobRepo, currentUser, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        var result = await handler.HandleAsync(new ExportJobItemsQuery(id), ct);
        var items = result.Value.Select(i => i.ToContract(systemKeys)).ToList();

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Results.File(json, "application/json", $"import-{id:N}.json");
        }

        byte[] csv = System.Text.Encoding.UTF8.GetBytes(BuildCsv(items));
        return Results.File(csv, "text/csv", $"import-{id:N}.csv");
    }

    private static string BuildCsv(IReadOnlyList<Models.JobItemDto> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("filename,kind,outcome,sizeBytes,platform,matchedTitles,gameCount,archiveOnly,error");

        foreach (var item in items)
        {
            string titles = string.Join("; ", item.MatchedTitles.Select(t => t.Name));
            builder.Append(Csv(item.FileName)).Append(',')
                .Append(Csv(item.Kind)).Append(',')
                .Append(Csv(item.Outcome)).Append(',')
                .Append(item.SizeBytes).Append(',')
                .Append(Csv(item.PlatformName ?? string.Empty)).Append(',')
                .Append(Csv(titles)).Append(',')
                .Append(item.GameCount?.ToString() ?? string.Empty).Append(',')
                .Append(item.ArchiveOnly?.ToString().ToLowerInvariant() ?? string.Empty).Append(',')
                .Append(Csv(item.Error ?? string.Empty)).Append('\n');
        }

        return builder.ToString();
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private static async Task<IResult> Archive(
        Guid id,
        IJobRepository jobRepo,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var job = await GetAuthorizedJobAsync(id, jobRepo, currentUser, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (!job.IsTerminal)
        {
            return ProblemResults.Problem(
                StatusCodes.Status400BadRequest,
                "Jobs.NotTerminal",
                "Cannot archive an active job.");
        }

        job.Archive();
        if (job is ArtworkImportJob)
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            await jobRepo.UpdateAsync(job, ct);
            await transaction.CommitAsync(ct);
        }
        else await jobRepo.UpdateAsync(job, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> Cancel(
        Guid id,
        IJobRepository jobRepo,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IBackgroundJobClient backgroundJobs,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var job = await GetAuthorizedJobAsync(id, jobRepo, currentUser, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.IsTerminal)
        {
            return ProblemResults.Problem(
                StatusCodes.Status400BadRequest,
                "Jobs.AlreadyComplete",
                "Job is already complete.");
        }

        job.Cancel();
        // Once authorization and the current state have been read, cancellation is a durable
        // boundary independent of the HTTP request lifetime. Persist Cancelled (which also clears
        // an enrichment fence) before signaling Hangfire so a shutdown runner can never race a
        // still-active database row or strand the job if transport deletion fails.
        if (job is ArtworkImportJob)
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            await jobRepo.UpdateAsync(job, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        else await jobRepo.UpdateAsync(job, CancellationToken.None);

        if (!string.IsNullOrEmpty(job.HangfireJobId))
        {
            try
            {
                backgroundJobs.Delete(job.HangfireJobId);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Romd.JobCancellation").LogWarning(
                    ex,
                    "Job {JobId} was durably cancelled but Hangfire delivery {HangfireJobId} could not be deleted",
                    job.Id,
                    job.HangfireJobId);
            }
        }

        return Results.NoContent();
    }

    private static async Task<IResult> Delete(
        Guid id,
        IJobRepository jobRepo,
        CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (!job.IsArchived)
        {
            return ProblemResults.Problem(
                StatusCodes.Status400BadRequest,
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
        CancellationToken ct)
    {
        int archivedCount = await jobRepo.ArchiveCompletedAsync(ct);
        return Results.Ok(new Models.ClearCompleted(archivedCount));
    }

    private static async Task<IResult> RetryFailed(
        Guid id,
        ICommandHandler<RetryFailedItemsCommand, Models.RetryFailedResult> handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RetryFailedItemsCommand(id), ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<Job?> GetAuthorizedJobAsync(
        Guid id,
        IJobRepository jobRepo,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var job = await jobRepo.GetByIdAsync(id, ct);
        if (job is null)
        {
            return null;
        }

        return currentUser.HasRole(RomdRoleType.Manager) || job.CreatedByUserId == currentUser.UserId
            ? job
            : null;
    }
}
