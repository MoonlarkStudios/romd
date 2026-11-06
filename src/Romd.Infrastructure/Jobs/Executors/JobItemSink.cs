using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Executors;

/// <summary>
///     Thread-safe buffer for per-file provenance. Items are enqueued from the parallel ROM loop
///     and flushed in batches; each flush uses its own DI scope (own DbContext) so concurrent
///     flushes never share an EF context.
/// </summary>
internal sealed class JobItemSink
{
    private readonly ConcurrentQueue<JobItem> _pending = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Action _onProgress;

    public JobItemSink(IServiceScopeFactory scopeFactory, Action onProgress)
    {
        _scopeFactory = scopeFactory;
        _onProgress = onProgress;
    }

    public void Add(JobItem item) => _pending.Enqueue(item);

    public async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<JobItem>();
        while (_pending.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobItemRepository>();
        try
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            {
                await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
                await repository.AddRangeStagedAsync(batch, ct);
                await transaction.CommitAsync(ct);
            }
            _onProgress(); // Only durable outcomes replenish the no-progress retry budget.
        }
        catch
        {
            // Retrying the same item IDs is idempotent even if the prior commit's
            // acknowledgement was lost.
            foreach (var item in batch)
                _pending.Enqueue(item);
            throw;
        }
    }
}
