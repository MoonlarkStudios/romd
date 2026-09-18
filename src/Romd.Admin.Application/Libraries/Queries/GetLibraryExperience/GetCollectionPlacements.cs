using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;

public sealed record GetCollectionPlacementsQuery(int CollectionId) : IQuery<IReadOnlyList<CollectionLibraryPlacementDto>>;
public sealed class GetCollectionPlacementsQueryHandler(ILibraryExperienceRepository experience)
    : IQueryHandler<GetCollectionPlacementsQuery, IReadOnlyList<CollectionLibraryPlacementDto>>
{
    public async Task<ErrorOr<IReadOnlyList<CollectionLibraryPlacementDto>>> HandleAsync(GetCollectionPlacementsQuery query, CancellationToken ct = default) =>
        (await experience.GetCollectionPlacementsAsync(query.CollectionId, ct)).ToList();
}
