using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Taxonomy.ReadModels;

namespace Romd.Admin.Application.Taxonomy.Queries.ListLanguages;

public sealed record ListLanguagesQuery : IQuery<IReadOnlyList<LanguageWithAliases>>;

public sealed class ListLanguagesQueryHandler : IQueryHandler<ListLanguagesQuery, IReadOnlyList<LanguageWithAliases>>
{
    private readonly ILanguageRepository _repository;

    public ListLanguagesQueryHandler(ILanguageRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<IReadOnlyList<LanguageWithAliases>>> HandleAsync(
        ListLanguagesQuery query, CancellationToken ct = default)
    {
        var items = await _repository.GetAllWithAliasesAsync(ct);
        return items.Select(x => new LanguageWithAliases(
            x.Entity.Id, x.Entity.Name, x.Entity.Code, x.Entity.SortOrder, x.Entity.IsAutoCreated, x.CanMerge, x.Aliases)).ToList();
    }
}
