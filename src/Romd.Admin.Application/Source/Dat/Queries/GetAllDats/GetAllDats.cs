using ErrorOr;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Source.Dat.Queries.GetAllDats;

/// <summary>
///     Query to get all DAT files with their stored source-file sizes.
/// </summary>
public sealed record GetAllDatsQuery : IQuery<IReadOnlyList<DatWithSize>>;

/// <summary>
///     Handler for <see cref="GetAllDatsQuery" />.
/// </summary>
public sealed class GetAllDatsQueryHandler : IQueryHandler<GetAllDatsQuery, IReadOnlyList<DatWithSize>>
{
    private readonly IDatRepository _repository;

    public GetAllDatsQueryHandler(IDatRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<IReadOnlyList<DatWithSize>>> HandleAsync(GetAllDatsQuery query,
        CancellationToken ct = default)
    {
        var dats = await _repository.GetAllWithSizeAsync(ct);
        return ErrorOrFactory.From(dats);
    }
}
