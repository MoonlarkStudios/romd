using Romd.Domain.Catalog.Ratings;

namespace Romd.Domain.Libraries;

/// <summary>
///     Input record representing a title and all candidate DatGame releases for access projection.
/// </summary>
public sealed record TitleCandidates(
    int TitleId,
    int PlatformId,
    string? Genre,
    IReadOnlyList<ContentRating> ContentRatings,
    IReadOnlyList<GameCandidate> Candidates);

/// <summary>
///     A single DatGame release candidate for access projection.
/// </summary>
public sealed record GameCandidate(
    int DatGameId,
    int? CatalogReleaseId,
    int DatFileId,
    string? Revision,
    bool HasOwnedRoms,
    bool IsComplete,
    IReadOnlyList<int> SourceDatFileIds,
    IReadOnlyList<int> RegionIds,
    IReadOnlyList<int> LanguageIds);
