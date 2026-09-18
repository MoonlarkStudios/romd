using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Game.Queries.GetGameById;

/// <summary>
///     Query to get a game by ID within a specific DAT.
/// </summary>
public sealed record GetGameByIdQuery : IQuery<DatGame>
{
    /// <summary>
    ///     The parent DAT file ID.
    /// </summary>
    public required int DatId { get; init; }

    /// <summary>
    ///     The game ID.
    /// </summary>
    public required int GameId { get; init; }
}

/// <summary>
///     Handler for <see cref="GetGameByIdQuery" />.
/// </summary>
public sealed class GetGameByIdQueryHandler : IQueryHandler<GetGameByIdQuery, DatGame>
{
    private readonly IDatRepository _repository;

    public GetGameByIdQueryHandler(IDatRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<DatGame>> HandleAsync(GetGameByIdQuery query, CancellationToken ct = default)
    {
        // Verify the DAT exists
        var datFile = await _repository.GetByIdAsync(query.DatId, ct);
        if (datFile is null)
        {
            return CatalogErrors.DatNotFound(query.DatId);
        }

        // Get game and verify it belongs to this DAT
        var game = await _repository.GetGameByIdAsync(query.GameId, ct);
        if (game is null || game.DatFileId != query.DatId)
        {
            return CatalogErrors.GameNotFound(query.GameId);
        }

        return game;
    }
}
