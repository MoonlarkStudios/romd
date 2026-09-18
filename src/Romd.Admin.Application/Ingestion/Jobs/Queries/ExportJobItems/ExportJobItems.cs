using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;

public sealed record ExportJobItemsQuery(Guid JobId) : IQuery<IReadOnlyList<JobItemView>>;

/// <summary>
///     Owns the read transaction for the capped export snapshot while its bounded title/platform
///     resolutions execute. Repositories remain transaction-agnostic.
/// </summary>
public sealed class ExportJobItemsQueryHandler(
    IJobItemRepository repository,
    IReadSnapshotTransactionFactory readSnapshots) : IQueryHandler<ExportJobItemsQuery, IReadOnlyList<JobItemView>>
{
    public async Task<ErrorOr<IReadOnlyList<JobItemView>>> HandleAsync(
        ExportJobItemsQuery query,
        CancellationToken ct = default)
    {
        await using var transaction = await readSnapshots.BeginAsync(ct);
        var result = await repository.GetAllByJobAsync(query.JobId, ct);
        await transaction.CompleteAsync(ct);
        return ErrorOrFactory.From(result);
    }
}
