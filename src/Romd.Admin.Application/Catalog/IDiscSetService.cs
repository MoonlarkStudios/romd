using ErrorOr;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Catalog;

public interface IDiscSetService
{
    Task<ErrorOr<LibraryDiscSetOptionsDto>> ListLibraryOptionsAsync(int libraryId, IReadOnlySet<int> eligibleReleaseIds, IReadOnlyList<int> selectedIds, string? query, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<Page<DiscSetDto>>> ListAsync(int? titleId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<DiscSetDto>> GetAsync(int id, CancellationToken ct);
}
