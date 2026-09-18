using System.Text.Json;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Realtime;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Realtime;

namespace Romd.Infrastructure.Jobs;

public sealed class HangfireJobStateSyncFilter(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<HangfireJobStateSyncFilter> logger) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is not FailedState failedState ||
            !TryGetRomdJobId(context.BackgroundJob.Job.Args, out var jobId))
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            using var applicationTransaction = dbContext.Database.BeginTransaction();
            // Serialize the transport failure with checkpoints and claim handoffs before reading.
            dbContext.Jobs.Where(job => job.Id == jobId)
                .ExecuteUpdate(setters => setters.SetProperty(job => job.UpdatedAt, job => job.UpdatedAt));
            var job = dbContext.Jobs
                .AsTracking()
                .FirstOrDefault(j => j.Id == jobId);

            if (job is null)
            {
                logger.LogWarning(
                    "Hangfire job {HangfireJobId} failed, but ROMD job {JobId} was not found",
                    context.BackgroundJob.Id,
                    jobId);
                return;
            }

            if (job.CompletedAt is not null
                || job.HangfireJobId != context.BackgroundJob.Id
                || (job.ExecutionFenceToken != null && job.ExecutionLeaseExpiresAtUtc > timeProvider.GetUtcNow()))
            {
                return;
            }

            job.Phase = "Failed";
            job.ExecutionFenceToken = null;
            job.ExecutionLeaseExpiresAtUtc = null;
            var failedAt = new DateTimeOffset(failedState.FailedAt);

            job.CompletedAt = failedAt;
            job.CurrentItem = null;
            job.ErrorsJson = AppendFailure(job.ErrorsJson, GetFailureMessage(failedState), failedAt);
            var now = timeProvider.GetUtcNow();
            job.UpdatedAt = now;

            dbContext.AdminRealtimeOutboxEvents.Add(new AdminRealtimeOutboxEventEntity
            {
                EventType = AdminRealtimeEventTypes.JobUpdated,
                PayloadJson = AdminRealtimePayloadSerializer.Serialize(ToDomain(job).ToContract(new Romd.Application.Common.Systems.SystemKeys(dbContext.Platforms.Where(x => x.CanonicalKey != null).ToDictionary(x => x.Id, x => x.CanonicalKey!)))),
                SchemaVersion = AdminRealtimeSchemaVersions.Initial,
                CreatedAtUtc = now,
                AvailableAtUtc = now
            });

            if (job is ArtworkImportJobEntity artwork)
                dbContext.ArtworkSelections
                    .Where(selection => selection.TitleId == artwork.TitleId && selection.Role == artwork.Role &&
                        selection.Revision == artwork.SelectionRevision && selection.PendingRequestId == artwork.Id)
                    .ExecuteUpdate(setters => setters.SetProperty(selection => selection.PendingRequestId, (Guid?)null));

            dbContext.SaveChanges();
            applicationTransaction.Commit();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to sync Hangfire failed state for job {HangfireJobId}",
                context.BackgroundJob.Id);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    private static bool TryGetRomdJobId(IReadOnlyList<object> args, out Guid jobId)
    {
        jobId = default;
        return args.Count > 0 && args[0] is Guid id && (jobId = id) != Guid.Empty;
    }

    private static string GetFailureMessage(FailedState failedState)
        => failedState.Exception.Message is { Length: > 0 } message
            ? message
            : failedState.Reason ?? "Hangfire job failed";

    private static string AppendFailure(string errorsJson, string message, DateTimeOffset failedAt)
    {
        List<JobError> errors;

        try
        {
            errors = JsonSerializer.Deserialize<List<JobError>>(errorsJson) ?? [];
        }
        catch (JsonException)
        {
            errors = [];
        }

        errors.Add(new JobError("job", message, failedAt));
        return JsonSerializer.Serialize(errors);
    }

    private static Job ToDomain(JobEntity entity) =>
        entity switch
        {
            UploadJobEntity upload => upload.ToDomain(),
            ReplaceDatJobEntity replace => replace.ToDomain(),
            EnrichmentJobEntity enrichment => enrichment.ToDomain(),
            BulkEnrichmentJobEntity bulk => bulk.ToDomain(),
            ExportJobEntity export => export.ToDomain(),
            MaterializationJobEntity materialization => materialization.ToDomain(),
            ArtworkImportJobEntity artwork => artwork.ToDomain(),
            _ => throw new InvalidOperationException($"Unknown job type: {entity.JobType}")
        };
}
