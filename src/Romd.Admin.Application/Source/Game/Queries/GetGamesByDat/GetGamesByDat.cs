using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Game.Queries.GetGamesByDat;

/// <summary>
///     Query to get a paged list of games for a specific DAT.
/// </summary>
public sealed record GetGamesByDatQuery : IQuery<PagedList<DatGame>>
{
    /// <summary>
    ///     The DAT file ID.
    /// </summary>
    public required int DatId { get; init; }

    /// <summary>
    ///     The cursor for pagination, or null for the first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    ///     Maximum number of items to return.
    /// </summary>
    public int Limit { get; init; } = 50;

    /// <summary>
    ///     Filter for BIOS entries. Defaults to excluding BIOS.
    /// </summary>
    public BiosFilter BiosFilter { get; init; } = BiosFilter.Exclude;
}

/// <summary>
///     Handler for <see cref="GetGamesByDatQuery" />.
/// </summary>
public sealed class GetGamesByDatQueryHandler : IQueryHandler<GetGamesByDatQuery, PagedList<DatGame>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IDatRepository _repository;

    public GetGamesByDatQueryHandler(
        IDatRepository repository,
        ICurrentUser currentUser)
    {
        _repository = repository;
        _currentUser = currentUser;
    }

    public async Task<ErrorOr<PagedList<DatGame>>> HandleAsync(GetGamesByDatQuery query, CancellationToken ct = default)
    {
        // Verify the DAT exists
        var datFile = await _repository.GetByIdAsync(query.DatId, ct);
        if (datFile is null)
        {
            return CatalogErrors.DatNotFound(query.DatId);
        }

        // Resolve library ID for user (Contributors+ bypass filtering)
        int? libraryId = _currentUser.HasRole(RomdRoleType.Contributor) ? null : _currentUser.LibraryId;

        return await _repository.GetGamesByDatIdAsync(
            query.DatId, query.Cursor, query.Limit, query.BiosFilter, libraryId, ct);
    }
}
