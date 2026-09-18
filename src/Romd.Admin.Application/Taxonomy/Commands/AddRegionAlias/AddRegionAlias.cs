using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.AddRegionAlias;

public sealed record AddRegionAliasCommand(int RegionId, string Alias) : ICommand<Created>;

public sealed class AddRegionAliasCommandHandler(TaxonomyService<Region> service)
    : ICommandHandler<AddRegionAliasCommand, Created>
{
    public Task<ErrorOr<Created>> HandleAsync(AddRegionAliasCommand command, CancellationToken ct = default) =>
        service.AddAliasAsync(command.RegionId, command.Alias, ct);
}
