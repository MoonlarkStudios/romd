using ErrorOr;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy.Commands.AddRegionAlias;

public sealed record AddRegionAliasCommand(string RegionKey, string Alias) : ICommand<TaxonomyAlias>;

public sealed class AddRegionAliasCommandHandler(TaxonomyService<Region> service)
    : ICommandHandler<AddRegionAliasCommand, TaxonomyAlias>
{
    public Task<ErrorOr<TaxonomyAlias>> HandleAsync(AddRegionAliasCommand command, CancellationToken ct = default) =>
        service.AddAliasAsync(command.RegionKey, command.Alias, ct);
}
