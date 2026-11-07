using Romd.Application.Common.Systems;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Activity;
using Romd.Domain.Activity;

using ContractPlaySession = Romd.Contracts.Consumer.Activity.PlaySession;

using PlaySession = Romd.Domain.Activity.PlaySession;

namespace Romd.Consumer.Application.Activity;

internal static class PlayActivityMapping
{
    public static ContractPlaySession ToContract(this PlaySession session) => new()
    {
        SessionId = session.SessionId,
        ClientId = session.ClientId,
        TitleId = IdCoder.Encode(session.TitleId),
        ReleaseId = IdCoder.Encode(session.ReleaseId),
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        ActiveDurationSeconds = session.ActiveDurationSeconds,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt
    };

    public static Page<ContractPlaySession> ToContract(this PagedList<PlaySession> page) => new()
    {
        Items = page.Items.Select(ToContract).ToArray(),
        NextCursor = page.NextCursor,
        HasNextPage = page.HasNextPage
    };

    public static RecentlyPlayedTitle ToContract(this RecentlyPlayedData item) => new()
    {
        Id = IdCoder.Encode(item.TitleId),
        Artwork = item.Artwork.Select(ArtworkContractMapping.ToContract).ToArray(),
        System = item.System.ToContract(),
        Name = item.Name,
        CoverUrl = item.CoverMediaId is { } mediaId ? $"/media/{IdCoder.Encode(mediaId)}" : null,
        Genre = item.Genre,
        ReleaseDate = item.ReleaseDate,
        Rating = item.Rating,
        ReleaseCount = item.ReleaseCount,
        DefaultReleaseId = item.DefaultReleaseId is { } releaseId ? IdCoder.Encode(releaseId) : null,
        LastPlayedAt = item.LastPlayedAt,
        PlayCount = item.PlayCount
    };
}
