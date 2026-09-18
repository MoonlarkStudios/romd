using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Source.Dat.Queries.GetDatById;

/// <summary>
///     Query to get a DAT file by its ID, with its stored source-file size.
/// </summary>
public sealed record GetDatByIdQuery(int DatId) : IQuery<DatWithSize>;

/// <summary>
///     Handler for <see cref="GetDatByIdQuery" />.
/// </summary>
public sealed class GetDatByIdQueryHandler : IQueryHandler<GetDatByIdQuery, DatWithSize>
{
    private readonly IDatRepository _repository;

    public GetDatByIdQueryHandler(IDatRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<DatWithSize>> HandleAsync(GetDatByIdQuery query, CancellationToken ct = default)
    {
        var datFile = await _repository.GetByIdWithSizeAsync(query.DatId, ct);
        return datFile is null ? CatalogErrors.DatNotFound(query.DatId) : datFile;
    }
}
