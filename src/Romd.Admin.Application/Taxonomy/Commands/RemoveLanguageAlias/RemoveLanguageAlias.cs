using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.RemoveLanguageAlias;

public sealed record RemoveLanguageAliasCommand(string LanguageKey, int AliasId) : ICommand;

public sealed class RemoveLanguageAliasCommandHandler(TaxonomyService<GameLanguage> service)
    : ICommandHandler<RemoveLanguageAliasCommand, Deleted>
{
    public Task<ErrorOr<Deleted>> HandleAsync(RemoveLanguageAliasCommand command, CancellationToken ct = default) =>
        service.RemoveAliasAsync(command.LanguageKey, command.AliasId, ct);
}
