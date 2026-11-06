using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.MergeLanguage;

public sealed record MergeLanguageCommand(string SourceKey, string TargetKey) : ICommand;

public sealed class MergeLanguageCommandHandler(TaxonomyService<GameLanguage> service)
    : ICommandHandler<MergeLanguageCommand, Deleted>
{
    public Task<ErrorOr<Deleted>> HandleAsync(MergeLanguageCommand command, CancellationToken ct = default) =>
        service.MergeAsync(command.SourceKey, command.TargetKey, ct);
}
