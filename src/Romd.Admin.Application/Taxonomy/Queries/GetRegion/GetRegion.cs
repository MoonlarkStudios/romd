using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Taxonomy.Queries.GetRegion;

public sealed record GetRegionQuery(string Key) : IQuery<TaxonomyEntry>;

/// <summary>Reads the row and its aliases in one repeatable-read snapshot, so the revision versions what it describes.</summary>
public sealed class GetRegionQueryHandler(IRegionRepository repository, IReadSnapshotTransactionFactory readSnapshots,
    ILogger<GetRegionQueryHandler>? logger = null)
    : IQueryHandler<GetRegionQuery, TaxonomyEntry>
{
    public async Task<ErrorOr<TaxonomyEntry>> HandleAsync(GetRegionQuery query, CancellationToken ct = default)
    {
        var entry = await readSnapshots.ExecuteReadSnapshotAsync(
            token => repository.GetWithAliasesAsync(query.Key, token), logger, ct);
        if (entry is null)
            return TaxonomyErrors.NotFound();
        return entry;
    }
}
