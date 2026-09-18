using ErrorOr;
using Romd.Domain.Libraries;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryExperience;

public sealed record GetLibraryPreviewQuery(int LibraryId, int? CollectionId, int AfterId, int AfterOrder) : IQuery<LibraryPreviewDto>;
public sealed class GetLibraryPreviewQueryHandler(ILibraryRepository libraries, ILibraryExperienceRepository experience)
    : IQueryHandler<GetLibraryPreviewQuery, LibraryPreviewDto>
{
    public async Task<ErrorOr<LibraryPreviewDto>> HandleAsync(GetLibraryPreviewQuery query, CancellationToken ct = default)
    {
        var library = await libraries.GetByIdAsync(query.LibraryId, ct);
        if (library is null) return LibraryErrors.NotFound();
        if (library.ConfigurationState != LibraryConfigurationState.Valid || library.NeedsMaterialization)
            return new LibraryPreviewDto([], null);
        return await experience.GetPreviewAsync(query.LibraryId, query.CollectionId, query.AfterId, query.AfterOrder, ct);
    }
}
