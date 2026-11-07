using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Taxonomy;
using Romd.Domain.Taxonomy;

using Region = Romd.Domain.Taxonomy.Region;

namespace Romd.Admin.Application.Taxonomy.Queries.PreviewRegionMerge;

public sealed record PreviewRegionMergeQuery(string SourceKey, string TargetKey) : IQuery<TaxonomyMergeImpact>;

public sealed class PreviewRegionMergeQueryHandler(TaxonomyService<Region> service)
    : IQueryHandler<PreviewRegionMergeQuery, TaxonomyMergeImpact>
{
    public Task<ErrorOr<TaxonomyMergeImpact>> HandleAsync(PreviewRegionMergeQuery query, CancellationToken ct = default) =>
        service.PreviewMergeAsync(query.SourceKey, query.TargetKey, ct);
}
