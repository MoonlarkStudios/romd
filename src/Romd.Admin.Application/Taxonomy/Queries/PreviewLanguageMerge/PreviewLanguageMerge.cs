using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Taxonomy;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Queries.PreviewLanguageMerge;

public sealed record PreviewLanguageMergeQuery(string SourceKey, string TargetKey) : IQuery<TaxonomyMergeImpact>;

public sealed class PreviewLanguageMergeQueryHandler(TaxonomyService<GameLanguage> service)
    : IQueryHandler<PreviewLanguageMergeQuery, TaxonomyMergeImpact>
{
    public Task<ErrorOr<TaxonomyMergeImpact>> HandleAsync(PreviewLanguageMergeQuery query, CancellationToken ct = default) =>
        service.PreviewMergeAsync(query.SourceKey, query.TargetKey, ct);
}
