using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.MergeLanguage;

public sealed record MergeLanguageCommand(int SourceId, int TargetId) : ICommand;

public sealed class MergeLanguageCommandHandler(TaxonomyService<GameLanguage> service)
    : ICommandHandler<MergeLanguageCommand, Deleted>
{
    public Task<ErrorOr<Deleted>> HandleAsync(MergeLanguageCommand command, CancellationToken ct = default) =>
        service.MergeAsync(command.SourceId, command.TargetId, ct);
}
