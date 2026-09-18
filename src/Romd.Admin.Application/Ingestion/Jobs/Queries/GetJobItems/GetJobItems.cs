using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;

public sealed record GetJobItemsQuery(
    Guid JobId,
    JobItemOutcome? Outcome,
    int Limit,
    Guid? Cursor = null,
    string? Search = null) : IQuery<JobItemPageResult>;

/// <summary>
///     Owns the read transaction that keeps the item snapshot and its bounded title/platform
///     resolutions on one database generation. Repositories remain transaction-agnostic.
/// </summary>
public sealed class GetJobItemsQueryHandler(
    IJobItemRepository repository,
    IReadSnapshotTransactionFactory readSnapshots) : IQueryHandler<GetJobItemsQuery, JobItemPageResult>
{
    public async Task<ErrorOr<JobItemPageResult>> HandleAsync(
        GetJobItemsQuery query,
        CancellationToken ct = default)
    {
        await using var transaction = await readSnapshots.BeginAsync(ct);
        var result = await repository.GetByJobAsync(query.JobId, query.Outcome, query.Limit, ct, query.Cursor, query.Search);
        await transaction.CompleteAsync(ct);
        return ErrorOrFactory.From(result);
    }
}
