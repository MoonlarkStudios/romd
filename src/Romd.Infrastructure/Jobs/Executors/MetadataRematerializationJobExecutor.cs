using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Jobs;
namespace Romd.Infrastructure.Jobs.Executors;

public sealed class MetadataRematerializationJobExecutor(
    IMetadataRematerializationTitles titles, IServiceScopeFactory scopes, TimeProvider clock) : IResumableJobExecutor<MetadataRematerializationJob>
{
    public async Task ExecuteAsync(MetadataRematerializationJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        int platformId = job.PlatformId ?? throw new InvalidOperationException("Metadata job requires a system.");
        if (job.CursorTitleId == 0)
        {
            job.SetTotalTitles(await titles.CountAsync(platformId, ct));
            await context.CheckpointAsync(ct);
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await titles.ReadPageAsync(platformId, job.CursorTitleId, 500, ct);
            if (page.Count == 0)
                return;
            foreach (int titleId in page)
            {
                ct.ThrowIfCancellationRequested();
                await context.EnsureExecutionOwnershipAsync(ct);
                string? failure = null;
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IRematerializationService>().RematerializeTitleAsync(titleId, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (JobExecutionOwnershipLostException) { throw; }
                catch (Exception exception)
                {
                    // An isolated fallback transaction must commit required title work before this cursor advances.
                    using var scope = scopes.CreateScope();
                    await context.EnsureExecutionOwnershipAsync(ct);
                    var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    await using var transaction = await unit.BeginTransactionAsync(ct);
                    await scope.ServiceProvider.GetRequiredService<IRematerializationScheduler>().EnqueueTitleAsync(titleId, ct);
                    await transaction.CommitAsync(ct);
                    failure = exception.Message;
                }
                await context.EnsureExecutionOwnershipAsync(ct);
                job.RecordProcessed(titleId, occurredAt: clock.GetUtcNow(), failure);
                await context.CheckpointAsync(ct);
            }
        }
    }
}
