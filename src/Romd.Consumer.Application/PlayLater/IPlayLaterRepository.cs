using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.PlayLater;

public interface IPlayLaterRepository
{
    Task<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>> ListPlayLaterTitlesAsync(
        ConsumerLibraryScope scope, ConsumerReleasePreference releasePreference, string? cursor, int limit,
        CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<IReadOnlyList<int>>> GetPlayLaterMembershipAsync(
        ConsumerLibraryScope scope, int[] titleIds, CancellationToken ct = default);

    /// <summary>Stages membership in the caller's transaction, returning false if the title is unavailable.</summary>
    Task<bool> StagePlayLaterMembershipAsync(
        ConsumerLibraryScope scope, int titleId, DateTimeOffset? addedAt, CancellationToken ct = default);
}
