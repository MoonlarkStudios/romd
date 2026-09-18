using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.RemoveRegionAlias;

public sealed record RemoveRegionAliasCommand(int RegionId, int AliasId) : ICommand;

public sealed class RemoveRegionAliasCommandHandler(TaxonomyService<Region> service)
    : ICommandHandler<RemoveRegionAliasCommand, Deleted>
{
    public Task<ErrorOr<Deleted>> HandleAsync(RemoveRegionAliasCommand command, CancellationToken ct = default) =>
        service.RemoveAliasAsync(command.RegionId, command.AliasId, ct);
}
