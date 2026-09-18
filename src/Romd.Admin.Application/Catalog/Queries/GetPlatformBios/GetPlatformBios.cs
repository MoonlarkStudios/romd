using ErrorOr;
using Romd.Admin.Application.Source.Platform;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Catalog.Queries.GetPlatformBios;

/// <summary>
///     Query for a platform's BIOS/firmware entries with ownership status.
/// </summary>
public sealed record GetPlatformBiosQuery(int PlatformId) : IQuery<IReadOnlyList<BiosOwnership>>;

public sealed class GetPlatformBiosQueryHandler
    : IQueryHandler<GetPlatformBiosQuery, IReadOnlyList<BiosOwnership>>
{
    private readonly IBiosRepository _biosRepository;
    private readonly IPlatformRepository _platformRepository;

    public GetPlatformBiosQueryHandler(
        IPlatformRepository platformRepository,
        IBiosRepository biosRepository)
    {
        _platformRepository = platformRepository;
        _biosRepository = biosRepository;
    }

    public async Task<ErrorOr<IReadOnlyList<BiosOwnership>>> HandleAsync(
        GetPlatformBiosQuery query,
        CancellationToken ct = default)
    {
        var platform = await _platformRepository.GetByIdAsync(query.PlatformId, ct);
        if (platform is null)
        {
            return CatalogErrors.PlatformNotFound(query.PlatformId);
        }

        var bios = await _biosRepository.GetByPlatformWithOwnershipAsync(query.PlatformId, ct);
        return ErrorOrFactory.From(bios);
    }
}
