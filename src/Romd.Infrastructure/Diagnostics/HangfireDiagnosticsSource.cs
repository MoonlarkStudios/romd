using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Diagnostics;

namespace Romd.Infrastructure.Diagnostics;

public interface IHangfireDiagnosticsSource
{
    bool IsAvailable { get; }

    HangfireDiagnosticsSourceData Read(
        int serverTake,
        int recurringJobTake,
        IReadOnlyList<string> queues,
        CancellationToken ct = default);
}

public sealed record HangfireDiagnosticsSourceData(
    IReadOnlyList<HangfireServerDiagnosticsData> Servers,
    IReadOnlyList<RecurringJobDiagnosticsData> RecurringJobs,
    IReadOnlyList<QueueBacklogDiagnosticsData> QueueBacklogs);

public sealed class HangfireDiagnosticsSource(IServiceProvider serviceProvider) : IHangfireDiagnosticsSource
{
    public bool IsAvailable => ResolveStorage() is not null;

    public HangfireDiagnosticsSourceData Read(
        int serverTake,
        int recurringJobTake,
        IReadOnlyList<string> queues,
        CancellationToken ct = default)
    {
        var storage = ResolveStorage()
            ?? throw new InvalidOperationException("Hangfire storage is not registered in this host.");
        var monitoring = storage.GetMonitoringApi();
        // Hangfire's provider-neutral monitoring API exposes Servers() only as a whole
        // snapshot; it has no count or paging overload. Materialization is therefore
        // unavoidable here. Bound all subsequent processing immediately, and keep this
        // limitation pinned by HangfireDiagnosticsSourceTests.
        var serverSnapshot = monitoring.Servers();
        var servers = serverSnapshot
            .OrderBy(server => server.Name, StringComparer.Ordinal)
            .Take(serverTake)
            .Select(server => new HangfireServerDiagnosticsData(
                server.Name,
                server.WorkersCount,
                server.Queues
                    .Take(OperationalDiagnosticsPolicy.HangfireServerQueueLimit)
                    .ToList(),
                ToUtc(server.StartedAt),
                ToUtc(server.Heartbeat)))
            .ToList();

        using var connection = storage.GetConnection();
        if (connection is not JobStorageConnection boundedConnection)
        {
            throw new InvalidOperationException(
                "The configured Hangfire provider does not expose bounded recurring-job reads.");
        }

        // GetRecurringJobs() without ids materializes the entire recurring-job set. Page the
        // provider-neutral set first, then hydrate only the bounded inventory requested by the
        // diagnostics contract.
        var recurringJobIds = boundedConnection.GetRangeFromSet(
            "recurring-jobs",
            startingFrom: 0,
            endingAt: recurringJobTake - 1)
            .Take(recurringJobTake)
            .ToList();
        ct.ThrowIfCancellationRequested();
        var recurringJobs = connection.GetRecurringJobs(recurringJobIds)
            .Take(recurringJobTake)
            .Select(job => new RecurringJobDiagnosticsData(
                job.Id,
                job.Queue,
                job.Cron,
                ToUtc(job.LastExecution),
                ToUtc(job.NextExecution),
                job.LastJobState,
                job.Error))
            .ToList();

        var queueBacklogs = new List<QueueBacklogDiagnosticsData>(queues.Count);
        foreach (string queue in queues)
        {
            ct.ThrowIfCancellationRequested();
            queueBacklogs.Add(new QueueBacklogDiagnosticsData(
                queue,
                monitoring.EnqueuedCount(queue),
                monitoring.FetchedCount(queue)));
        }

        return new HangfireDiagnosticsSourceData(servers, recurringJobs, queueBacklogs);
    }

    private JobStorage? ResolveStorage() => serviceProvider.GetService<JobStorage>();

    private static DateTimeOffset? ToUtc(DateTime? value) => value.HasValue
        ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
        : null;
}
