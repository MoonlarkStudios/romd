using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;

namespace Romd.Admin.Application.Libraries.Queries.GetLibraryTitleReleaseDiagnostics;

public sealed record GetLibraryTitleReleaseDiagnosticsQuery(int LibraryId, int TitleId, string? Cursor = null, int Limit = 50) : IQuery<Page<LibraryTitleReleaseDiagnosticsDto>>;
public sealed class GetLibraryTitleReleaseDiagnosticsQueryHandler(ILibraryRepository repository) : IQueryHandler<GetLibraryTitleReleaseDiagnosticsQuery, Page<LibraryTitleReleaseDiagnosticsDto>>
{
    public async Task<ErrorOr<Page<LibraryTitleReleaseDiagnosticsDto>>> HandleAsync(GetLibraryTitleReleaseDiagnosticsQuery query, CancellationToken ct = default)
    {
        var result = await repository.GetReleaseDiagnosticsPageAsync(query.LibraryId, query.TitleId, query.Cursor, query.Limit, ct);
        if (result.IsError)
            return result.Errors;
        return new Page<LibraryTitleReleaseDiagnosticsDto> { Items = result.Value.Items.Select(item => item.ToContract()).ToArray(), NextCursor = result.Value.NextCursor, HasNextPage = result.Value.HasNextPage };
    }
}
