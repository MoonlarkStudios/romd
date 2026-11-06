using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.MergeRegion;

public sealed record MergeRegionCommand(string SourceKey, string TargetKey) : ICommand;

public sealed class MergeRegionCommandHandler(TaxonomyService<Region> service)
    : ICommandHandler<MergeRegionCommand, Deleted>
{
    public Task<ErrorOr<Deleted>> HandleAsync(MergeRegionCommand command, CancellationToken ct = default) =>
        service.MergeAsync(command.SourceKey, command.TargetKey, ct);
}
