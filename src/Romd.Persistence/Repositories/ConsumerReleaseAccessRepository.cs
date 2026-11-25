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
            (libraryId, token) => GetAccessForLibraryAsync(libraryId, request.ReleaseId, null, token),
            ct);

    public Task<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>> GetPlaySessionAccessAsync(
        ConsumerReleaseAccessRequest request,
        int requestedTitleId,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadInTransactionAsync<ConsumerReleaseAccessDecision>(
            request.Scope,
            (libraryId, token) => GetAccessForLibraryAsync(libraryId, request.ReleaseId, requestedTitleId, token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerReleaseAccessDecision>> GetAccessForLibraryAsync(
        int libraryId,
        int releaseId,
        int? requestedTitleId,
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

        bool allowed = projection is { IsOwned: true, IsExposed: true }
            && (requestedTitleId is null || requestedTitleId == projection.TitleId ||
                await context.TitleMergeRedirects.AnyAsync(redirect =>
                    redirect.SourceTitleId == requestedTitleId && redirect.TargetTitleId == projection.TitleId, ct));
        var decision = allowed
            ? ConsumerReleaseAccessDecision.Allow(projection.TitleId)
            : ConsumerReleaseAccessDecision.Revoke();
        return new ConsumerLibraryProjectionResult<ConsumerReleaseAccessDecision>.Found(decision);
    }
}
