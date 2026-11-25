using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Titles.Queries.GetTitleLookupNames;

public sealed record GetTitleLookupNamesQuery(int TitleId) : IQuery<TitleLookupNameSnapshot>;

public sealed class GetTitleLookupNamesQueryHandler(ITitleLookupNameStore store)
    : IQueryHandler<GetTitleLookupNamesQuery, TitleLookupNameSnapshot>
{
    public async Task<ErrorOr<TitleLookupNameSnapshot>> HandleAsync(GetTitleLookupNamesQuery query, CancellationToken ct = default)
    {
        var snapshot = await store.ReadAsync(query.TitleId, ct);
        if (snapshot is null)
            return CatalogErrors.TitleNotFound();
        return snapshot;
    }
}
