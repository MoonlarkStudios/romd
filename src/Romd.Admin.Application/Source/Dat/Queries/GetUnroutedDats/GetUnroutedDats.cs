using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat.Queries.GetUnroutedDats;

/// <summary>
///     An unrouted DAT (no platform assigned) together with the number of
///     stored ROM files matched to its entries — the work blocked behind
///     routing it — and its catalog source's lifecycle status and id.
/// </summary>
public sealed record UnroutedDatSummary(
    DatFile DatFile,
    int MatchedRomFileCount,
    CatalogSourceStatus SourceStatus,
    int CatalogSourceId);

/// <summary>
///     Query for all unrouted DATs with their blocked-ROM counts.
/// </summary>
public sealed record GetUnroutedDatsQuery : IQuery<IReadOnlyList<UnroutedDatSummary>>;

public sealed class GetUnroutedDatsQueryHandler
    : IQueryHandler<GetUnroutedDatsQuery, IReadOnlyList<UnroutedDatSummary>>
{
    private readonly IDatRepository _repository;

    public GetUnroutedDatsQueryHandler(IDatRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<IReadOnlyList<UnroutedDatSummary>>> HandleAsync(
        GetUnroutedDatsQuery query,
        CancellationToken ct = default)
    {
        // Each row carries its source status and catalog id from one repository snapshot;
        // counts remain a separate lookup because absent counts legitimately default to zero.
        var dats = await _repository.GetUnroutedAsync(ct);
        if (dats.Count == 0)
        {
            return ErrorOrFactory.From<IReadOnlyList<UnroutedDatSummary>>([]);
        }

        var counts = await _repository.CountMatchedRomFilesByDatAsync(
            dats.Select(d => d.Dat.Id).ToList(),
            ct);

        var summaries = dats
            .Select(d => new UnroutedDatSummary(
                d.Dat,
                counts.GetValueOrDefault(d.Dat.Id),
                d.SourceStatus,
                d.CatalogSourceId))
            .ToList();

        return ErrorOrFactory.From<IReadOnlyList<UnroutedDatSummary>>(summaries);
    }
}
