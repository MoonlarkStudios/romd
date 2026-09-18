using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using Romd.Infrastructure.Jobs.Handlers;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Jobs;

/// <summary>At-least-once transport of committed dispatch intents; the job ID is the stable identity.</summary>
public sealed class JobDispatchService(
    JobDispatchRepository repository,
    IBackgroundJobClient backgroundJobs,
    ILogger<JobDispatchService> logger)
{
    public async Task DispatchAsync(Guid jobId, CancellationToken ct = default)
    {
        var dispatch = await repository.TryClaimAsync(jobId, ct);
        if (dispatch is null) return;
        try
        {
            var (invocation, queue) = CreateInvocation(dispatch.JobType, dispatch.JobId);
            string deliveryId = backgroundJobs.Create(invocation, new EnqueuedState(queue));
            if (string.IsNullOrWhiteSpace(deliveryId))
                throw new InvalidOperationException("Hangfire did not return a delivery ID.");
            await repository.AcknowledgeAsync(dispatch, deliveryId, ct);
            logger.LogInformation("Dispatched job {JobId} ({JobType}), attempt {Attempt}, delivery {DeliveryId}",
                jobId, dispatch.JobType, dispatch.AttemptCount, deliveryId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave the claim to expire: visibility may have preceded shutdown, so redelivery is safe.
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Dispatch failed for job {JobId} ({JobType}), attempt {Attempt}",
                jobId, dispatch.JobType, dispatch.AttemptCount);
            await repository.RetryAsync(dispatch, exception.Message, ct);
        }
    }

    internal static (Job Invocation, string Queue) CreateInvocation(string jobType, Guid jobId) => jobType switch
    {
        "upload" => (Job.FromExpression<UploadJobHangfireHandler>(h => h.ExecuteAsync(jobId, null!)), JobQueues.Upload),
        "replace_dat" => (Job.FromExpression<ReplaceDatJobHangfireHandler>(h => h.ExecuteAsync(jobId, null!)), JobQueues.Upload),
        "export" => (Job.FromExpression<ExportJobHangfireHandler>(h => h.ExecuteAsync(jobId, null!)), JobQueues.Default),
        "enrichment" => (Job.FromExpression<EnrichmentJobHangfireHandler>(h => h.ExecuteAsync(jobId, null!)), JobQueues.Enrichment),
        "bulk_enrichment" => (Job.FromExpression<BulkEnrichmentJobHangfireHandler>(h => h.ExecuteAsync(jobId, null!)), JobQueues.Enrichment),
        "materialization" => (Job.FromExpression<MaterializationJobHangfireHandler>(h => h.ExecuteAsync(jobId, null!)), JobQueues.Materialization),
        "artwork-import" => (Job.FromExpression<ArtworkImportJobHangfireHandler>(h => h.ExecuteAsync(jobId, null!)), JobQueues.Enrichment),
        _ => throw new InvalidOperationException($"Unknown job dispatch type '{jobType}'.")
    };
}
