using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryDiscSetOptions;

public sealed record GetLibraryDiscSetOptionsQuery(int LibraryId, string? Cursor = null, int Limit = 100, string? Query = null) : IQuery<LibraryDiscSetOptions>;
public sealed class GetLibraryDiscSetOptionsQueryHandler(ILibraryRepository libraries, ILibraryCandidateReader candidates, IDiscSetService sets)
    : IQueryHandler<GetLibraryDiscSetOptionsQuery, LibraryDiscSetOptions>
{
    public async Task<ErrorOr<LibraryDiscSetOptions>> HandleAsync(GetLibraryDiscSetOptionsQuery query, CancellationToken ct = default)
    {
        var library = await libraries.GetByIdAsync(query.LibraryId, ct);
        if (library is null)
            return LibraryErrors.NotFound();
        var eligible = library.HasValidConfiguration ? MaterializedLibraryProjectionBuilder.Build(library.Id,
            await candidates.GetCandidatesAsync(library.Configuration, ct), library.Configuration).Releases
            .Where(x => x.IsEligible && !x.IsBlocked && x.IsExposed && x.CatalogReleaseId != null)
            .Select(x => x.CatalogReleaseId!.Value).ToHashSet() : [];
        return await sets.ListLibraryOptionsAsync(library.Id, eligible, library.Configuration.SelectedDiscSetIds, query.Query, query.Cursor, query.Limit, ct);
    }
}
