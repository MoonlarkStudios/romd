using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Artwork;

namespace Romd.Contracts.Consumer.Activity;

public sealed record UpsertPlaySessionRequest
{
    public required string ClientId { get; init; }
    public required string TitleId { get; init; }
    public required string ReleaseId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int? ActiveDurationSeconds { get; init; }
}

public sealed record PlaySessionDto
{
    public required Guid SessionId { get; init; }
    public required string ClientId { get; init; }
    public required string TitleId { get; init; }
    public required string ReleaseId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int? ActiveDurationSeconds { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RecentlyPlayedTitleDto
{
    public IReadOnlyList<ResolvedArtworkDto> Artwork { get; init; } = [];
    public required string Id { get; init; }
    public required SystemSummaryDto System { get; init; }
    public required string Name { get; init; }
    public string? CoverUrl { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public double? Rating { get; init; }
    public required int ReleaseCount { get; init; }
    public string? DefaultReleaseId { get; init; }
    public required DateTimeOffset LastPlayedAt { get; init; }
    public required int PlayCount { get; init; }
}
