using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryGenreFacets;

public sealed record GetLibraryGenreFacetsQuery(int LibraryId, int MinimumItems, string? Cursor = null, int Limit = 50) : IQuery<Page<LibraryFacetDto>>;
public sealed class GetLibraryGenreFacetsQueryHandler(ILibraryRepository repository) : IQueryHandler<GetLibraryGenreFacetsQuery, Page<LibraryFacetDto>>
{
    public async Task<ErrorOr<Page<LibraryFacetDto>>> HandleAsync(GetLibraryGenreFacetsQuery query, CancellationToken ct = default)
    {
        var result = await repository.GetGenrePageAsync(query.LibraryId, query.MinimumItems, query.Cursor, query.Limit, ct);
        if (result.IsError)
            return result.Errors;
        return new Page<LibraryFacetDto> { Items = result.Value.Items.Select(item => item.ToContract()).ToArray(), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage };
    }
}
