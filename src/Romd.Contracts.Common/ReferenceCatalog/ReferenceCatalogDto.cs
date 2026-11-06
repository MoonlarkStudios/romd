namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>Display facts only. Presence never grants access or emulator capability.</summary>
public sealed record ReferenceCatalogDto
{
    public int SchemaVersion { get; init; } = 1;
    public required int BuiltInVersion { get; init; }
    public required string Revision { get; init; }
    public required IReadOnlyList<ReferenceSystemDto> Systems { get; init; }
    public required IReadOnlyList<ReferenceRatingBoardDto> RatingBoards { get; init; }
    public required IReadOnlyList<ReferenceRatingDto> Ratings { get; init; }
    public IReadOnlyList<ReferenceNamedDto> Companies { get; init; } = [];
    public IReadOnlyList<ReferenceTaxonomyDto> Regions { get; init; } = [];
    public IReadOnlyList<ReferenceTaxonomyDto> Languages { get; init; } = [];
}
public sealed record ReferenceAssetDto(string Url, string Sha256, string ContentType, bool Monochrome);
public sealed record ReferenceSystemDto(string Key, string Name, string CompactLabel,
    string? Description, ReferenceAssetDto? Icon, IReadOnlyList<string>? ManufacturerKeys = null, bool Retired = false, IReadOnlyList<CompanySummaryDto>? Manufacturers = null);
public sealed record ReferenceRatingBoardDto(string Key, string Name, string? Description, bool Retired = false);
public sealed record ReferenceRatingDto(string Board, string Code, string Name, string? Description, ReferenceAssetDto? Icon, string? Designation = null, int? MinimumAge = null, bool Retired = false);

public sealed record ReferenceNamedDto(string Key, string Name, string? Description, bool Retired = false);
public sealed record ReferenceTaxonomyDto(string Key, string Name, string? Description, int SortOrder, string? LanguageCode, bool Retired = false);
