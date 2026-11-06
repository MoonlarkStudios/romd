using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy.Queries.ListLanguages;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Taxonomy.Queries.ListLanguageAliases;

/// <summary>One page of a language's aliases in ascending id order.</summary>
public sealed record ListLanguageAliasesQuery(string Key, string? Cursor = null, int Limit = TaxonomyAliasPageRequest.DefaultLimit)
    : IQuery<PagedList<TaxonomyAlias>>;

/// <summary>Resolves the language and reads its page in one repeatable-read snapshot.</summary>
public sealed class ListLanguageAliasesQueryHandler(ILanguageRepository repository, IReadSnapshotTransactionFactory readSnapshots,
    ILogger<ListLanguageAliasesQueryHandler>? logger = null)
    : IQueryHandler<ListLanguageAliasesQuery, PagedList<TaxonomyAlias>>
{
    public async Task<ErrorOr<PagedList<TaxonomyAlias>>> HandleAsync(ListLanguageAliasesQuery query, CancellationToken ct = default)
    {
        var request = TaxonomyAliasPageRequest.Create(ListLanguagesQueryHandler.Collection, query.Key, query.Cursor, query.Limit);
        if (request.IsError)
            return request.Errors;

        var page = await readSnapshots.ExecuteReadSnapshotAsync<PagedList<TaxonomyAlias>?>(async token =>
            await repository.GetIdByKeyAsync(query.Key, token) is { } id
                ? await repository.ListAliasesAsync(id, request.Value, token)
                : null, logger, ct);
        if (page is null)
            return TaxonomyErrors.NotFound();
        return page;
    }
}
