using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Libraries;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerReleaseAccessRepository(RomdDbContext context)
    : IConsumerReleaseAccessRepository
{
    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);

    public Task<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>> GetAccessAsync(
        ConsumerReleaseAccessRequest request,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<ConsumerReleaseAccessDecision>(
            request.Scope,
            (libraryId, token) => GetAccessForLibraryAsync(libraryId, request.ReleaseId, token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerReleaseAccessDecision>> GetAccessForLibraryAsync(
        int libraryId,
        int releaseId,
        CancellationToken ct)
    {
        var rows = await context.MaterializedLibraryReleases
            .AsNoTracking()
            .Where(release =>
                release.LibraryId == libraryId &&
                release.CatalogReleaseId == releaseId)
            .Select(release => new
            {
                release.TitleId,
                release.PlatformId,
                release.IsOwned,
                release.IsExposed
            })
            .Take(2)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return new ConsumerLibraryProjectionResult<ConsumerReleaseAccessDecision>.Found(
                ConsumerReleaseAccessDecision.Revoke());
        }

        if (rows.Count != 1)
        {
            return new ConsumerLibraryProjectionResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent();
        }

        var projection = rows[0];
        var catalogReleases = await context.CatalogReleases
            .AsNoTracking()
            .Where(release => release.Id == releaseId)
            .Select(release => new
            {
                release.CatalogTitleId,
                release.PlatformId
            })
            .Take(2)
            .ToListAsync(ct);

        if (catalogReleases.Count != 1 ||
            catalogReleases[0].CatalogTitleId != projection.TitleId ||
            catalogReleases[0].PlatformId != projection.PlatformId)
        {
            return new ConsumerLibraryProjectionResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent();
        }

        var decision = projection is { IsOwned: true, IsExposed: true }
            ? ConsumerReleaseAccessDecision.Allow(projection.TitleId)
            : ConsumerReleaseAccessDecision.Revoke();
        return new ConsumerLibraryProjectionResult<ConsumerReleaseAccessDecision>.Found(decision);
    }
}
