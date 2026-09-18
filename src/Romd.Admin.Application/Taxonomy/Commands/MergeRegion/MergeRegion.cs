using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.MergeRegion;

public sealed record MergeRegionCommand(int SourceId, int TargetId) : ICommand;

public sealed class MergeRegionCommandHandler(TaxonomyService<Region> service)
    : ICommandHandler<MergeRegionCommand, Deleted>
{
    public Task<ErrorOr<Deleted>> HandleAsync(MergeRegionCommand command, CancellationToken ct = default) =>
        service.MergeAsync(command.SourceId, command.TargetId, ct);
}
