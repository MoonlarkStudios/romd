using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.Source.Platform.Commands.RemovePlatformAlias;

/// <summary>
///     Command to remove an alias from a platform.
/// </summary>
public sealed record RemovePlatformAliasCommand(int PlatformId, int AliasId) : ICommand<Deleted>;

public sealed class RemovePlatformAliasCommandHandler : ICommandHandler<RemovePlatformAliasCommand, Deleted>
{
    private readonly IPlatformAliasRepository _aliasRepository;
    private readonly IPlatformRepository _platformRepository;

    public RemovePlatformAliasCommandHandler(
        IPlatformRepository platformRepository,
        IPlatformAliasRepository aliasRepository)
    {
        _platformRepository = platformRepository;
        _aliasRepository = aliasRepository;
    }

    public async Task<ErrorOr<Deleted>> HandleAsync(RemovePlatformAliasCommand command, CancellationToken ct = default)
    {
        var platform = await _platformRepository.GetByIdAsync(command.PlatformId, ct);
        if (platform is null)
        {
            return CatalogErrors.PlatformNotFound();
        }

        if (await _aliasRepository.RemoveInstallationOwnedAsync(command.PlatformId, command.AliasId, ct))
            return Result.Deleted;

        var alias = await _aliasRepository.GetByIdAsync(command.AliasId, ct);
        return alias is not null && alias.PlatformId == command.PlatformId && alias.Ownership != ReferenceOwnership.Installation
            ? PlatformErrors.AliasLocked()
            : PlatformErrors.AliasNotFound();
    }
}
