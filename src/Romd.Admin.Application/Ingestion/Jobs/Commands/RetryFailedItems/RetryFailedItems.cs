using ErrorOr;
using Romd.Admin.Application.Titles.Commands.QueueTitleEnrichmentJob;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;
using Romd.Domain.Jobs;

using BulkEnrichmentJob = Romd.Domain.Jobs.BulkEnrichmentJob;
using MetadataRematerializationJob = Romd.Domain.Jobs.MetadataRematerializationJob;

namespace Romd.Admin.Application.Ingestion.Jobs.Commands.RetryFailedItems;

public sealed record RetryFailedItemsCommand(Guid JobId) : ICommand<RetryFailedResult>;

public static class RetryFailedItemsErrors
{
    public static Error JobNotFound(Guid jobId) =>
        Error.NotFound("Job.NotFound", $"Job {jobId} was not found.");

    public static Error NotRetryable =>
        Error.Validation("Job.NotRetryable", "This job has no retryable failures.");
}

/// <summary>
///     Re-enqueues the failed items of a completed job as a fresh, scoped retry.
///     Today only bulk enrichment records retryable items (failed titles); the
///     original job's record is left untouched so history stays an honest log.
/// </summary>
public sealed class RetryFailedItemsCommandHandler(
    IJobRepository jobRepo,
    ICommandHandler<QueueTitleEnrichmentJobCommand, Success> acceptance) : ICommandHandler<RetryFailedItemsCommand, RetryFailedResult>
{
    public async Task<ErrorOr<RetryFailedResult>> HandleAsync(
        RetryFailedItemsCommand command,
        CancellationToken ct = default)
    {
        var job = await jobRepo.GetByIdAsync(command.JobId, ct);
        if (job is null)
        {
            return RetryFailedItemsErrors.JobNotFound(command.JobId);
        }

        if (job is MetadataRematerializationJob)
            return Error.Conflict("MetadataRematerialization.RetryUnsupported", "Deferred titles are already queued for retry.");

        if (job is not BulkEnrichmentJob || job.PlatformId is not { } platformId)
        {
            return RetryFailedItemsErrors.NotRetryable;
        }

        var titles = job.Errors
            .Where(error => error.EntityId.HasValue)
            .DistinctBy(error => error.EntityId!.Value)
            .Select(error => (TitleId: error.EntityId!.Value, TitleName: error.Item, PlatformId: platformId))
            .ToList();

        if (titles.Count == 0)
        {
            return RetryFailedItemsErrors.NotRetryable;
        }

        // Each title owns an independent acceptance commit, preserving progress if a later item fails.
        foreach (var title in titles)
        {
            var result = await acceptance.HandleAsync(new(title.TitleId, title.TitleName, title.PlatformId), ct);
            if (result.IsError)
                return result.Errors;
        }

        return new RetryFailedResult(titles.Count);
    }
}
