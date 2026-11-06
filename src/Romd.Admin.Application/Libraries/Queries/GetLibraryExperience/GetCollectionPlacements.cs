using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;

public sealed record GetCollectionPlacementsQuery(int CollectionId, string? Cursor = null, int Limit = 50) : IQuery<Page<CollectionLibraryPlacementDto>>;
public sealed class GetCollectionPlacementsQueryHandler(ILibraryExperienceRepository experience) : IQueryHandler<GetCollectionPlacementsQuery, Page<CollectionLibraryPlacementDto>>
{
    public Task<ErrorOr<Page<CollectionLibraryPlacementDto>>> HandleAsync(GetCollectionPlacementsQuery query, CancellationToken ct = default) =>
        experience.GetCollectionPlacementsPageAsync(query.CollectionId, query.Cursor, query.Limit, ct);
}
