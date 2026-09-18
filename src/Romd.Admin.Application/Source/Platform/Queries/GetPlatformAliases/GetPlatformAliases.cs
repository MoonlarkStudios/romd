using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.Source.Platform.Queries.GetPlatformAliases;

/// <summary>
///     Query for all aliases of a platform.
/// </summary>
public sealed record GetPlatformAliasesQuery(int PlatformId) : IQuery<IReadOnlyList<PlatformAlias>>;

public sealed class GetPlatformAliasesQueryHandler
    : IQueryHandler<GetPlatformAliasesQuery, IReadOnlyList<PlatformAlias>>
{
    private readonly IPlatformAliasRepository _aliasRepository;
    private readonly IPlatformRepository _platformRepository;

    public GetPlatformAliasesQueryHandler(
        IPlatformRepository platformRepository,
        IPlatformAliasRepository aliasRepository)
    {
        _platformRepository = platformRepository;
        _aliasRepository = aliasRepository;
    }

    public async Task<ErrorOr<IReadOnlyList<PlatformAlias>>> HandleAsync(
        GetPlatformAliasesQuery query,
        CancellationToken ct = default)
    {
        var platform = await _platformRepository.GetByIdAsync(query.PlatformId, ct);
        if (platform is null)
        {
            return CatalogErrors.PlatformNotFound(query.PlatformId);
        }

        var aliases = await _aliasRepository.GetByPlatformIdAsync(query.PlatformId, ct);
        return ErrorOrFactory.From(aliases);
    }
}
