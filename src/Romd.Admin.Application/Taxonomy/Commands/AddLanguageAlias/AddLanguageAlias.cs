using ErrorOr;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.AddLanguageAlias;

public sealed record AddLanguageAliasCommand(string LanguageKey, string Alias) : ICommand<TaxonomyAlias>;

public sealed class AddLanguageAliasCommandHandler(TaxonomyService<GameLanguage> service)
    : ICommandHandler<AddLanguageAliasCommand, TaxonomyAlias>
{
    public Task<ErrorOr<TaxonomyAlias>> HandleAsync(AddLanguageAliasCommand command, CancellationToken ct = default) =>
        service.AddAliasAsync(command.LanguageKey, command.Alias, ct);
}
