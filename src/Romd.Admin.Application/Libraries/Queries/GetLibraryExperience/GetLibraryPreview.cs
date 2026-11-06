using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;

public sealed record GetLibraryPreviewQuery(int LibraryId, int? CollectionId, string? Cursor = null, int Limit = 48) : IQuery<Page<LibraryPreviewTitleDto>>;
public sealed class GetLibraryPreviewQueryHandler(ILibraryRepository libraries, ILibraryExperienceRepository experience) : IQueryHandler<GetLibraryPreviewQuery, Page<LibraryPreviewTitleDto>>
{
    public async Task<ErrorOr<Page<LibraryPreviewTitleDto>>> HandleAsync(GetLibraryPreviewQuery query, CancellationToken ct = default)
    {
        if (await libraries.GetByIdAsync(query.LibraryId, ct) is null)
            return LibraryErrors.NotFound();
        return await experience.GetPreviewPageAsync(query.LibraryId, query.CollectionId, query.Cursor, query.Limit, ct);
    }
}
