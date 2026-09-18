using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Taxonomy.ReadModels;

namespace Romd.Admin.Application.Taxonomy.Queries.ListRegions;

public sealed record ListRegionsQuery : IQuery<IReadOnlyList<RegionWithAliases>>;

public sealed class ListRegionsQueryHandler : IQueryHandler<ListRegionsQuery, IReadOnlyList<RegionWithAliases>>
{
    private readonly IRegionRepository _repository;

    public ListRegionsQueryHandler(IRegionRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<IReadOnlyList<RegionWithAliases>>> HandleAsync(
        ListRegionsQuery query, CancellationToken ct = default)
    {
        var items = await _repository.GetAllWithAliasesAsync(ct);
        return items.Select(x => new RegionWithAliases(
            x.Entity.Id, x.Entity.Name, x.Entity.SortOrder, x.Entity.IsAutoCreated, x.CanMerge, x.Aliases)).ToList();
    }
}
