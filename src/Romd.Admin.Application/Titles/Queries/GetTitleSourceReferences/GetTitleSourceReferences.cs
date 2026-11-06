using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Titles.Queries.GetTitleSourceReferences;

/// <summary>
///     Query for the distinct catalog sources backing a title. Truth-level: every status
///     is returned so curation surfaces can show dormant backing.
/// </summary>
public sealed record GetTitleSourceReferencesQuery(int TitleId) : IQuery<IReadOnlyList<TitleSourceReference>>;

/// <summary>
///     Handler for <see cref="GetTitleSourceReferencesQuery" />.
/// </summary>
public sealed class GetTitleSourceReferencesQueryHandler
    : IQueryHandler<GetTitleSourceReferencesQuery, IReadOnlyList<TitleSourceReference>>
{
    private readonly ITitleSourceReferenceReader _referenceReader;
    private readonly ITitleReader _titleReader;

    public GetTitleSourceReferencesQueryHandler(
        ITitleReader titleReader,
        ITitleSourceReferenceReader referenceReader)
    {
        _titleReader = titleReader;
        _referenceReader = referenceReader;
    }

    public async Task<ErrorOr<IReadOnlyList<TitleSourceReference>>> HandleAsync(
        GetTitleSourceReferencesQuery query,
        CancellationToken ct = default)
    {
        var title = await _titleReader.GetSummaryAsync(query.TitleId, ct);
        if (title is null)
        {
            return CatalogErrors.TitleNotFound();
        }

        var references = await _referenceReader.GetReferencesAsync(query.TitleId, ct);
        return ErrorOrFactory.From(references);
    }
}
