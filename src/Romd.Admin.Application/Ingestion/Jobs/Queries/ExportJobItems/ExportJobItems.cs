using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;

public sealed record ExportJobItemsQuery(Guid JobId) : IQuery<JobItemExport>;

/// <summary>
///     Opens and prefetches the export snapshot. The response owns the returned lease until its
///     final write completes; no lazy read may outlive the repeatable read transaction.
/// </summary>
public sealed class ExportJobItemsQueryHandler(
    IJobItemRepository repository,
    IReadSnapshotTransactionFactory readSnapshots, ILogger<ExportJobItemsQueryHandler>? logger = null) : IQueryHandler<ExportJobItemsQuery, JobItemExport>
{
    public async Task<ErrorOr<JobItemExport>> HandleAsync(
        ExportJobItemsQuery query,
        CancellationToken ct = default)
    {
        var transaction = await readSnapshots.BeginAsync(ct);
        try
        {
            var firstPage = await repository.GetByJobAsync(query.JobId, null, JobItemExport.BatchSize, ct);
            return new JobItemExport(repository, transaction, query.JobId, firstPage, logger);
        }
        catch (Exception primary)
        {
            await ReadSnapshotCleanup.DisposeAsync([transaction], false, primary, logger ?? NullLogger<ExportJobItemsQueryHandler>.Instance);
            throw;
        }
    }
}
