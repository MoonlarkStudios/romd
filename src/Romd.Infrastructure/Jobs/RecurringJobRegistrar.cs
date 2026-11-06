using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Realtime;

namespace Romd.Infrastructure.Jobs;

public sealed class RecurringJobRegistrar : IHostedService
{
    private readonly ILogger<RecurringJobRegistrar> _logger;
    private readonly IRecurringJobManager _recurringJobs;

    public RecurringJobRegistrar(
        IRecurringJobManager recurringJobs,
        ILogger<RecurringJobRegistrar> logger)
    {
        _recurringJobs = recurringJobs;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _recurringJobs.AddOrUpdate<DatSubscriptionCheckJob>(
            "dat-subscription-checks",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/15 * * * *");

        _recurringJobs.AddOrUpdate<OrphanedFileCleanupJob>(
            "orphaned-file-cleanup",
            job => job.ExecuteAsync(null!),
            Cron.Daily);

        _recurringJobs.AddOrUpdate<OrphanedJobCleanupJob>(
            "orphaned-job-cleanup",
            job => job.ExecuteAsync(null!),
            "*/30 * * * *");

        _recurringJobs.AddOrUpdate<DatReplacementConvergenceSweepJob>(
            "dat-replacement-convergence-sweep",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/30 * * * *");

        _recurringJobs.AddOrUpdate<TitlePayloadAvailabilitySweepJob>(
            "title-payload-availability-sweep",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/30 * * * *");

        _recurringJobs.AddOrUpdate<IJobRepository>(
            "purge-archived-jobs",
            repo => repo.PurgeArchivedAsync(TimeSpan.FromDays(30), CancellationToken.None),
            Cron.Daily(3));

        _recurringJobs.AddOrUpdate<ExportArtifactCleanupJob>(
            "export-artifact-cleanup",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(4));

        _recurringJobs.AddOrUpdate<AdminRealtimeOutboxCleanupJob>(
            "admin-realtime-outbox-cleanup",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(5));

        _recurringJobs.AddOrUpdate<RomdOpenIddictPruneJob>(
            "openiddict-prune",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(6));

        _logger.LogInformation("Registered recurring Hangfire jobs");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
