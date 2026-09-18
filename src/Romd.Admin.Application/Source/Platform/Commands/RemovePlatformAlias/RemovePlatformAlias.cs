using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;

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
            return CatalogErrors.PlatformNotFound(command.PlatformId);
        }

        var alias = await _aliasRepository.GetByIdAsync(command.AliasId, ct);
        if (alias is null || alias.PlatformId != command.PlatformId)
        {
            return PlatformErrors.AliasNotFound(command.AliasId);
        }

        await _aliasRepository.RemoveAsync(command.AliasId, ct);
        return Result.Deleted;
    }
}
