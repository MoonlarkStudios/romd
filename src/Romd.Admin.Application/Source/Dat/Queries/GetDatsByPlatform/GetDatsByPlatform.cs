using Romd.Application.Common.Pagination;
using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Source.Platform;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Source.Dat.Queries.GetDatsByPlatform;

/// <summary>
///     Query for a platform's Active DATs with their catalog source statuses.
/// </summary>
public sealed record GetDatsByPlatformQuery(int PlatformId, string? Cursor = null, int Limit = 50, string? Query = null) : IQuery<PagedList<DatWithSourceStatus>>;

/// <summary>
///     Handler for <see cref="GetDatsByPlatformQuery" />.
/// </summary>
public sealed class GetDatsByPlatformQueryHandler
    : IQueryHandler<GetDatsByPlatformQuery, PagedList<DatWithSourceStatus>>
{
    private readonly IDatRepository _datRepository;
    private readonly IPlatformRepository _platformRepository;

    public GetDatsByPlatformQueryHandler(IPlatformRepository platformRepository, IDatRepository datRepository)
    {
        _platformRepository = platformRepository;
        _datRepository = datRepository;
    }

    public async Task<ErrorOr<PagedList<DatWithSourceStatus>>> HandleAsync(
        GetDatsByPlatformQuery query,
        CancellationToken ct = default)
    {
        var platform = await _platformRepository.GetByIdAsync(query.PlatformId, ct);
        if (platform is null)
        {
            return CatalogErrors.PlatformNotFound();
        }

        // DAT and source status come from one repository snapshot, so a concurrent
        // DAT deletion can never leave a DAT without its status here.
        return await _datRepository.GetSystemPageAsync(query.PlatformId, query.Query, query.Cursor, query.Limit, ct);
    }
}
