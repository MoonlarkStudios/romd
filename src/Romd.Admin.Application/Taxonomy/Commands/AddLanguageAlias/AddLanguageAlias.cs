using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.AddLanguageAlias;

public sealed record AddLanguageAliasCommand(int LanguageId, string Alias) : ICommand<Created>;

public sealed class AddLanguageAliasCommandHandler(TaxonomyService<GameLanguage> service)
    : ICommandHandler<AddLanguageAliasCommand, Created>
{
    public Task<ErrorOr<Created>> HandleAsync(AddLanguageAliasCommand command, CancellationToken ct = default) =>
        service.AddAliasAsync(command.LanguageId, command.Alias, ct);
}
