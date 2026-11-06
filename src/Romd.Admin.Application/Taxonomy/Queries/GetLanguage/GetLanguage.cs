using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Taxonomy.Queries.GetLanguage;

public sealed record GetLanguageQuery(string Key) : IQuery<TaxonomyEntry>;

/// <summary>Reads the row and its aliases in one repeatable-read snapshot, so the revision versions what it describes.</summary>
public sealed class GetLanguageQueryHandler(ILanguageRepository repository, IReadSnapshotTransactionFactory readSnapshots,
    ILogger<GetLanguageQueryHandler>? logger = null)
    : IQueryHandler<GetLanguageQuery, TaxonomyEntry>
{
    public async Task<ErrorOr<TaxonomyEntry>> HandleAsync(GetLanguageQuery query, CancellationToken ct = default)
    {
        var entry = await readSnapshots.ExecuteReadSnapshotAsync(
            token => repository.GetWithAliasesAsync(query.Key, token), logger, ct);
        if (entry is null)
            return TaxonomyErrors.NotFound();
        return entry;
    }
}
