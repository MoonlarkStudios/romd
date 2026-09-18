using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Diagnostics;

namespace Romd.Admin.Application.Diagnostics.Queries.GetOperationalDiagnostics;

public sealed record GetOperationalDiagnosticsQuery : IQuery<OperationalDiagnosticsDto>;

public sealed class GetOperationalDiagnosticsQueryHandler(
    IServiceScopeFactory scopeFactory,
    OperationalDiagnosticsAdmission admission,
    TimeProvider timeProvider,
    OperationalDiagnosticsOptions options,
    ILogger<GetOperationalDiagnosticsQueryHandler> logger)
    : IQueryHandler<GetOperationalDiagnosticsQuery, OperationalDiagnosticsDto>
{
    public async Task<ErrorOr<OperationalDiagnosticsDto>> HandleAsync(
        GetOperationalDiagnosticsQuery query,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        using var overallBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallBudget.CancelAfter(options.OverallBudget);

        var projectionsTask = RunSectionAsync<IOperationalDiagnosticsReader,
            BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>>(
            "catalog projections",
            OperationalDiagnosticsSection.CatalogProjections,
            (reader, token) => reader.ReadCatalogProjectionsAsync(
                OperationalDiagnosticsPolicy.CatalogProjectionLimit,
                token),
            overallBudget.Token);
        var jobsTask = RunSectionAsync<IOperationalDiagnosticsReader, OperationalJobDiagnosticsData>(
            "jobs",
            OperationalDiagnosticsSection.Jobs,
            (reader, token) => reader.ReadJobsAsync(OperationalDiagnosticsPolicy.JobLimit, token),
            overallBudget.Token);
        var outboxTask = RunSectionAsync<IOperationalDiagnosticsReader, OutboxDiagnosticsData>(
            "outbox",
            OperationalDiagnosticsSection.Outbox,
            (reader, token) => reader.ReadOutboxAsync(token),
            overallBudget.Token);
        var hangfireTask = RunSectionAsync<IHangfireDiagnosticsReader, HangfireDiagnosticsData>(
            "Hangfire",
            OperationalDiagnosticsSection.Hangfire,
            (reader, token) => reader.ReadAsync(
                OperationalDiagnosticsPolicy.HangfireServerLimit,
                OperationalDiagnosticsPolicy.RecurringJobLimit,
                token),
            overallBudget.Token);
        var storageTask = RunSectionAsync<IStorageDiagnosticsReader, StorageDiagnosticsData>(
            "storage",
            OperationalDiagnosticsSection.Storage,
            (reader, token) => reader.ReadAsync(token),
            overallBudget.Token);

        await Task.WhenAll(projectionsTask, jobsTask, outboxTask, hangfireTask, storageTask);
        ct.ThrowIfCancellationRequested();
        var projections = await projectionsTask;
        var jobs = await jobsTask;
        var outbox = await outboxTask;
        var hangfire = await hangfireTask;
        var storage = await storageTask;

        return new OperationalDiagnosticsDto
        {
            GeneratedAt = now,
            CatalogProjections = new CatalogProjectionDiagnosticsDto
            {
                Availability = Availability(projections),
                Error = projections.Error,
                Items = projections.Value?.Items.Select(item => ToContract(item)).ToList() ?? [],
                IsTruncated = projections.Value?.IsTruncated ?? false
            },
            Jobs = new OperationalJobDiagnosticsDto
            {
                Availability = Availability(jobs),
                Error = jobs.Error,
                WedgedReplaceDatJobs = jobs.Value?.ReplaceDatJobs.Items
                    .Select(item => ToContract(item, now)).ToList() ?? [],
                WedgedReplaceDatJobsTruncated = jobs.Value?.ReplaceDatJobs.IsTruncated ?? false,
                StrandedBulkEnrichmentJobs = jobs.Value?.BulkEnrichmentJobs.Items
                    .Select(item => ToContract(item, now)).ToList() ?? [],
                StrandedBulkEnrichmentJobsTruncated = jobs.Value?.BulkEnrichmentJobs.IsTruncated ?? false
            },
            Outbox = new OutboxDiagnosticsDto
            {
                Availability = Availability(outbox),
                Error = outbox.Error,
                PendingCount = outbox.Value?.PendingCount ?? 0,
                OldestPendingAgeSeconds = AgeSeconds(now, outbox.Value?.OldestPendingAt),
                FailingCount = outbox.Value?.FailingCount ?? 0,
                LastProcessedAt = outbox.Value?.LastProcessedAt
            },
            Hangfire = new HangfireDiagnosticsDto
            {
                Availability = hangfire.Value is { IsAvailable: true }
                    ? DiagnosticAvailability.Available
                    : DiagnosticAvailability.Unavailable,
                Error = hangfire.Error ?? hangfire.Value?.Error,
                Servers = hangfire.Value?.Servers.Items.Select(item => ToContract(item, now)).ToList() ?? [],
                ServersTruncated = hangfire.Value?.Servers.IsTruncated ?? false,
                RecurringJobs = hangfire.Value?.RecurringJobs.Items.Select(ToContract).ToList() ?? [],
                RecurringJobsTruncated = hangfire.Value?.RecurringJobs.IsTruncated ?? false,
                QueueBacklogs = hangfire.Value?.QueueBacklogs.Select(ToContract).ToList() ?? []
            },
            Storage = new StorageDiagnosticsDto
            {
                DataVolumeAvailability = storage.Value is { IsDataVolumeAvailable: true }
                    ? DiagnosticAvailability.Available
                    : DiagnosticAvailability.Unavailable,
                DataVolumeFreeBytes = (ByteCount?)storage.Value?.DataVolumeFreeBytes,
                CasAvailability = storage.Value is { IsCasAvailable: true }
                    ? DiagnosticAvailability.Available
                    : DiagnosticAvailability.Unavailable,
                Error = storage.Error ?? storage.Value?.Error
            }
        };
    }

    private async Task<SectionResult<TResult>> RunSectionAsync<TReader, TResult>(
        string sectionName,
        OperationalDiagnosticsSection section,
        Func<TReader, CancellationToken, Task<TResult>> read,
        CancellationToken overallToken) where TReader : notnull
    {
        var lease = admission.TryAcquire(section);
        if (lease is null)
        {
            return new SectionResult<TResult>(
                default,
                $"The {sectionName} diagnostics probe is still running from an earlier request.");
        }

        var scope = scopeFactory.CreateScope();
        var budget = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
        Task<TResult>? operation = null;
        try
        {
            budget.CancelAfter(options.SectionBudget);
            // Some providers perform synchronous work before returning their Task. Keep that
            // work behind a scheduling boundary so every section is admitted and the response
            // budgets can expire even when a provider blocks before its first await.
            operation = Task.Factory.StartNew(
                    async () =>
                    {
                        var reader = scope.ServiceProvider.GetRequiredService<TReader>();
                        return await read(reader, budget.Token);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();
            var value = await operation.WaitAsync(budget.Token);
            return new SectionResult<TResult>(value, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Operational diagnostics section {SectionName} is unavailable", sectionName);
            string error = budget.IsCancellationRequested
                ? $"The {sectionName} diagnostics budget was exceeded."
                : ex.Message;
            return new SectionResult<TResult>(default, error);
        }
        finally
        {
            ReleaseWhenOperationCompletes(scope, budget, lease, operation, sectionName);
        }
    }

    private void ReleaseWhenOperationCompletes<TResult>(
        IServiceScope scope,
        CancellationTokenSource budget,
        OperationalDiagnosticsAdmission.Lease lease,
        Task<TResult>? operation,
        string sectionName)
    {
        if (operation is null)
        {
            budget.Dispose();
            scope.Dispose();
            lease.Dispose();
            return;
        }

        operation.ContinueWith(
            Release,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        void Release(Task? completed)
        {
            if (completed?.Exception is { } exception)
            {
                logger.LogDebug(
                    exception,
                    "Operational diagnostics section {SectionName} completed after its budget",
                    sectionName);
            }

            budget.Dispose();
            scope.Dispose();
            lease.Dispose();
        }
    }

    private static DiagnosticAvailability Availability<T>(SectionResult<T> section) =>
        section.Value is null ? DiagnosticAvailability.Unavailable : DiagnosticAvailability.Available;

    private static CatalogProjectionDiagnosticDto ToContract(CatalogProjectionDiagnosticsData item) => new()
    {
        SystemKey = item.SystemKey,
        PlatformName = item.PlatformName,
        PlatformShortName = item.PlatformShortName,
        State = (CatalogProjectionState)item.State,
        RebuiltAt = item.RebuiltAt,
        LastError = item.LastError,
        FailedAt = item.FailedAt
    };

    private static WedgedReplaceDatJobDto ToContract(ReplaceDatJobDiagnosticsData item, DateTimeOffset now) => new()
    {
        JobId = item.JobId,
        ExistingDatId = IdCoder.Encode(item.ExistingDatId),
        SystemKey = item.SystemKey,
        Phase = (ReplaceDatDiagnosticPhase)item.Phase,
        CreatedAt = item.CreatedAt,
        StartedAt = item.StartedAt,
        LastProgressAt = item.LastProgressAt,
        StalledForSeconds = AgeSeconds(now, item.LastProgressAt) ?? 0,
        AttemptError = item.AttemptError,
        AttemptErrorTruncated = item.AttemptErrorTruncated
    };

    private static StrandedBulkEnrichmentJobDto ToContract(
        BulkEnrichmentJobDiagnosticsData item,
        DateTimeOffset now) => new()
    {
        JobId = item.JobId,
        SystemKey = item.SystemKey,
        PlatformLabel = item.PlatformLabel,
        Phase = BulkEnrichmentDiagnosticPhase.Pending,
        CreatedAt = item.CreatedAt,
        StrandedForSeconds = AgeSeconds(now, item.CreatedAt) ?? 0
    };

    private static HangfireServerDiagnosticDto ToContract(
        HangfireServerDiagnosticsData item,
        DateTimeOffset now) => new()
    {
        Name = item.Name,
        WorkerCount = item.WorkerCount,
        Queues = item.Queues,
        StartedAt = item.StartedAt,
        HeartbeatAt = item.HeartbeatAt,
        HeartbeatAgeSeconds = AgeSeconds(now, item.HeartbeatAt)
    };

    private static RecurringJobDiagnosticDto ToContract(RecurringJobDiagnosticsData item) => new()
    {
        Id = item.Id,
        Queue = item.Queue,
        Cron = item.Cron,
        LastExecutionAt = item.LastExecutionAt,
        NextExecutionAt = item.NextExecutionAt,
        LastJobState = ToRecurringJobState(item.LastJobState),
        Error = item.Error
    };

    private static RecurringJobDiagnosticState? ToRecurringJobState(string? state) => state switch
    {
        null => null,
        "Awaiting" => RecurringJobDiagnosticState.Awaiting,
        "Scheduled" => RecurringJobDiagnosticState.Scheduled,
        "Enqueued" => RecurringJobDiagnosticState.Enqueued,
        "Processing" => RecurringJobDiagnosticState.Processing,
        "Succeeded" => RecurringJobDiagnosticState.Succeeded,
        "Failed" => RecurringJobDiagnosticState.Failed,
        "Deleted" => RecurringJobDiagnosticState.Deleted,
        _ => RecurringJobDiagnosticState.Unknown
    };

    private static QueueBacklogDiagnosticDto ToContract(QueueBacklogDiagnosticsData item) => new()
    {
        Queue = item.Queue,
        EnqueuedCount = item.EnqueuedCount,
        FetchedCount = item.FetchedCount
    };

    private static long? AgeSeconds(DateTimeOffset now, DateTimeOffset? timestamp) =>
        timestamp.HasValue ? Math.Max(0, (long)(now - timestamp.Value).TotalSeconds) : null;

    private sealed record SectionResult<T>(T? Value, string? Error);
}
