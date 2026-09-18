using Romd.Application.Common.Systems;
using Romd.Application.Common.Pagination;
using Romd.Domain.Activity;
using Romd.Domain.Catalog;

namespace Romd.Consumer.Application.Activity;

public enum PlaySessionUpsertStatus
{
    Created,
    Updated,
    Unchanged,
    Conflict
}

public sealed record PlaySessionUpsertResult(PlaySessionUpsertStatus Status, PlaySession? Session);

public sealed record RecentlyPlayedData(
    int TitleId,
    int PlatformId,
    SystemSummaryData System,
    string Name,
    int? CoverMediaId,
    string? Genre,
    DateOnly? ReleaseDate,
    double? Rating,
    int ReleaseCount,
    int? DefaultReleaseId,
    DateTimeOffset LastPlayedAt,
    int PlayCount,
    IReadOnlyList<ArtworkResolution> Artwork);

public interface IPlayActivityRepository
{
    Task<PlaySessionUpsertResult> UpsertAsync(
        Guid userId,
        Guid sessionId,
        PlaySessionSnapshot snapshot,
        CancellationToken ct = default);

    Task<PlaySession?> GetAccessibleAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    Task<PagedList<PlaySession>?> ListAccessibleAsync(
        Guid userId,
        string? cursor,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecentlyPlayedData>?> ListRecentlyPlayedAsync(
        Guid userId,
        int limit,
        CancellationToken ct = default);

    Task DeleteAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    Task ClearAsync(Guid userId, CancellationToken ct = default);
}
