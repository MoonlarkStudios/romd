using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Taxonomy;
namespace Romd.Admin.Application.Taxonomy.Queries.GetMergeImpact;
public sealed record GetMergeImpactQuery(string Kind, int SourceId, int TargetId) : IQuery<TaxonomyMergeImpactDto>;
public interface ITaxonomyMergeImpactReader
{
    Task<ErrorOr<TaxonomyMergeImpactDto>> ReadAsync(GetMergeImpactQuery query, CancellationToken ct);
}
public sealed class GetMergeImpactQueryHandler(ITaxonomyMergeImpactReader reader) : IQueryHandler<GetMergeImpactQuery, TaxonomyMergeImpactDto>
{
    public Task<ErrorOr<TaxonomyMergeImpactDto>> HandleAsync(GetMergeImpactQuery query, CancellationToken ct = default) => reader.ReadAsync(query, ct);
}
