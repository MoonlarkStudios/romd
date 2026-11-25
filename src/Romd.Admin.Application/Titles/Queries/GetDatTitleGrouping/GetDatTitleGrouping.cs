using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Titles.Queries.GetDatTitleGrouping;

public sealed record GetDatTitleGroupingQuery(int TitleId) : IQuery<DatTitleGroupingRead>;

public sealed class GetDatTitleGroupingQueryHandler(IDatTitleGroupingReader reader, IReadSnapshotTransactionFactory snapshots,
    ILogger<GetDatTitleGroupingQueryHandler> logger) : IQueryHandler<GetDatTitleGroupingQuery, DatTitleGroupingRead>
{
    public async Task<ErrorOr<DatTitleGroupingRead>> HandleAsync(GetDatTitleGroupingQuery query, CancellationToken ct = default)
    {
        var review = await snapshots.ExecuteReadSnapshotAsync(token => reader.ReadAsync(query.TitleId, token), logger, ct);
        if (review is null)
            return CatalogErrors.TitleNotFound();
        return review;
    }
}
