using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.ReadModels;

/// <summary>
///     Scalar title facts for lookups and lists. <see cref="HasLocalPayload" /> is the
///     projection-owned local-payload fact.
/// </summary>
public sealed record TitleSummary(
    int Id,
    int PlatformId,
    string Name,
    EnrichmentStatus EnrichmentStatus,
    string? Description,
    string? Publisher,
    string? Developer,
    string? Genre,
    DateOnly? ReleaseDate,
    int? Players,
    double? Rating,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastEnrichedAt,
    bool HasLocalPayload);
