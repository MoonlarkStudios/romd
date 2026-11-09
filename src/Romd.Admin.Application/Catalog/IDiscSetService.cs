using ErrorOr;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Catalog;

public interface IDiscSetService
{
    Task<ErrorOr<LibraryDiscSetOptions>> ListLibraryOptionsAsync(int libraryId, IReadOnlySet<int> eligibleReleaseIds, IReadOnlyList<int> selectedIds, string? query, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<Page<DiscSet>>> ListAsync(int? titleId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<DiscSet>> GetAsync(int id, CancellationToken ct);
}
