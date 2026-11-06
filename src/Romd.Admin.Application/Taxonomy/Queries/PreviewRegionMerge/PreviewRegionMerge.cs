using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Taxonomy;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Queries.PreviewRegionMerge;

public sealed record PreviewRegionMergeQuery(string SourceKey, string TargetKey) : IQuery<TaxonomyMergeImpactDto>;

public sealed class PreviewRegionMergeQueryHandler(TaxonomyService<Region> service)
    : IQueryHandler<PreviewRegionMergeQuery, TaxonomyMergeImpactDto>
{
    public Task<ErrorOr<TaxonomyMergeImpactDto>> HandleAsync(PreviewRegionMergeQuery query, CancellationToken ct = default) =>
        service.PreviewMergeAsync(query.SourceKey, query.TargetKey, ct);
}
