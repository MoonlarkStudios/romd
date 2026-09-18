using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryTitleReleaseDiagnostics;

public sealed record GetLibraryTitleReleaseDiagnosticsQuery(int LibraryId, int TitleId)
    : IQuery<IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>;

public sealed class GetLibraryTitleReleaseDiagnosticsQueryHandler(ILibraryRepository libraryRepository)
    : IQueryHandler<GetLibraryTitleReleaseDiagnosticsQuery, IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>
{
    public async Task<ErrorOr<IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>> HandleAsync(
        GetLibraryTitleReleaseDiagnosticsQuery query,
        CancellationToken ct = default)
    {
        if (await libraryRepository.GetByIdAsync(query.LibraryId, ct) is null)
        {
            return LibraryErrors.NotFound();
        }

        var releases = await libraryRepository.GetTitleReleaseDiagnosticsAsync(
            query.LibraryId,
            query.TitleId,
            ct);
        return releases.Select(release => release.ToContract()).ToList();
    }
}
