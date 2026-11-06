using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class JobCheckpointTestExtensions
{
    // Fixture callers own a durable checkpoint. Protocol tests exercise the staged port directly.
    internal static async Task CommitCheckpointAsync<TJob>(
        this IJobRepository<TJob> repository,
        RomdDbContext context,
        TJob job,
        CancellationToken ct = default) where TJob : Job
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(ct);
        await repository.UpdateStagedAsync(job, ct);
        await transaction.CommitAsync(ct);
    }

    internal static async Task CommitCheckpointAsync(
        this IJobRepository repository,
        RomdDbContext context,
        Job job,
        CancellationToken ct = default)
    {
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync(ct);
        await repository.UpdateStagedAsync(job, ct);
        await transaction.CommitAsync(ct);
    }
}
