using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Handlers;

/// <summary>
///     Generic Hangfire-to-pipeline bridge. Contains lifecycle orchestration only.
///     Not used directly by Hangfire — use the typed subclasses below, which carry
///     the [Queue] attribute so retries route to the correct queue.
/// </summary>
public sealed class JobHandler<TJob>(IServiceProvider serviceProvider) where TJob : Job
{
    public async Task ExecuteAsync(Guid jobId, PerformContext context)
    {
        var ct = context.CancellationToken.ShutdownToken;
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<JobRunner<TJob>>();
        await runner.RunAsync(jobId, context.BackgroundJob.Id, ct);
    }
}

/// <summary>
///     Hangfire entry point for upload jobs. Routes to the "upload" queue on retries.
/// </summary>
public sealed class UploadJobHangfireHandler(IServiceProvider sp)
{
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [Queue(JobQueues.Upload)]
    public Task ExecuteAsync(Guid jobId, PerformContext ctx)
        => new JobHandler<UploadJob>(sp).ExecuteAsync(jobId, ctx);
}

/// <summary>
///     Hangfire entry point for replace-DAT jobs. Routes to the "upload" queue on retries.
/// </summary>
public sealed class ReplaceDatJobHangfireHandler(IServiceProvider sp)
{
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [Queue(JobQueues.Upload)]
    public Task ExecuteAsync(Guid jobId, PerformContext ctx)
        => new JobHandler<ReplaceDatJob>(sp).ExecuteAsync(jobId, ctx);
}

/// <summary>
///     Hangfire entry point for enrichment jobs. Routes to the single-threaded
///     "enrichment" queue. No automatic retries — Polly handles transient HTTP failures,
///     and a failed enrichment should not retry the entire title lookup.
/// </summary>
public sealed class EnrichmentJobHangfireHandler(IServiceProvider sp)
{
    [AutomaticRetry(Attempts = 0)]
    [Queue(JobQueues.Enrichment)]
    public Task ExecuteAsync(Guid jobId, PerformContext ctx)
        => new JobHandler<EnrichmentJob>(sp).ExecuteAsync(jobId, ctx);
}

/// <summary>
///     Hangfire entry point for bulk enrichment jobs. Routes to the enrichment queue.
///     No automatic retries — individual title failures are tracked within the job.
/// </summary>
public sealed class BulkEnrichmentJobHangfireHandler(IServiceProvider sp)
{
    [AutomaticRetry(Attempts = 0)]
    [Queue(JobQueues.Enrichment)]
    public Task ExecuteAsync(Guid jobId, PerformContext ctx)
        => new JobHandler<BulkEnrichmentJob>(sp).ExecuteAsync(jobId, ctx);
}

/// <summary>
///     Hangfire entry point for export jobs. Routes to the default queue.
///     No automatic retries — export failures should not retry.
/// </summary>
public sealed class ExportJobHangfireHandler(IServiceProvider sp)
{
    [AutomaticRetry(Attempts = 0)]
    [Queue(JobQueues.Default)]
    public Task ExecuteAsync(Guid jobId, PerformContext ctx)
        => new JobHandler<ExportJob>(sp).ExecuteAsync(jobId, ctx);
}

/// <summary>
///     Hangfire entry point for library materialization jobs. Routes to the serial materialization queue.
///     Single automatic retry — materialization is idempotent (atomic replace).
/// </summary>
public sealed class MaterializationJobHangfireHandler(IServiceProvider sp)
{
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [Queue(JobQueues.Materialization)]
    public Task ExecuteAsync(Guid jobId, PerformContext ctx)
        => new JobHandler<MaterializationJob>(sp).ExecuteAsync(jobId, ctx);
}

/// <summary>Resumable artwork import; provider requests share the serial enrichment queue.</summary>
public sealed class ArtworkImportJobHangfireHandler(IServiceProvider sp)
{
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [Queue(JobQueues.Enrichment)]
    public Task ExecuteAsync(Guid jobId, PerformContext ctx)
        => new JobHandler<ArtworkImportJob>(sp).ExecuteAsync(jobId, ctx);
}
