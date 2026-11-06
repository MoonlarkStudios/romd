using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Taxonomy.Queries.ListLanguages;

/// <summary>One page of every language, registered or not, matching the query, in (sortOrder, name, key) order.</summary>
public sealed record ListLanguagesQuery(string? Cursor = null, int Limit = TaxonomyPageRequest.DefaultLimit, string? Query = null)
    : IQuery<PagedList<TaxonomyEntry>>;

/// <summary>Reads each page's rows and their aliases in one repeatable-read snapshot.</summary>
public sealed class ListLanguagesQueryHandler(ILanguageRepository repository, IReadSnapshotTransactionFactory readSnapshots,
    ILogger<ListLanguagesQueryHandler>? logger = null)
    : IQueryHandler<ListLanguagesQuery, PagedList<TaxonomyEntry>>
{
    public const string Collection = "languages";

    public async Task<ErrorOr<PagedList<TaxonomyEntry>>> HandleAsync(ListLanguagesQuery query, CancellationToken ct = default)
    {
        var request = TaxonomyPageRequest.Create(Collection, query.Cursor, query.Limit, query.Query);
        if (request.IsError)
            return request.Errors;

        var page = await readSnapshots.ExecuteReadSnapshotAsync(
            token => repository.ListWithAliasesAsync(request.Value, token), logger, ct);
        return page;
    }
}
