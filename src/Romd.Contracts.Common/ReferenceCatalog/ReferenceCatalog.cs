namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>Display facts only. Presence never grants access or emulator capability.</summary>
public sealed record ReferenceCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public required int BuiltInVersion { get; init; }
    public required string Revision { get; init; }
    public required IReadOnlyList<ReferenceSystem> Systems { get; init; }
    public required IReadOnlyList<ReferenceRatingBoard> RatingBoards { get; init; }
    public required IReadOnlyList<ReferenceRating> Ratings { get; init; }
    public IReadOnlyList<ReferenceNamed> Companies { get; init; } = [];
    public IReadOnlyList<ReferenceTaxonomy> Regions { get; init; } = [];
    public IReadOnlyList<ReferenceTaxonomy> Languages { get; init; } = [];
}
public sealed record ReferenceAsset(string Url, string Sha256, string ContentType, bool Monochrome);
public sealed record ReferenceSystem(string Key, string Name, string CompactLabel,
    string? Description, ReferenceAsset? Icon, IReadOnlyList<string>? ManufacturerKeys = null, bool Retired = false, IReadOnlyList<CompanySummary>? Manufacturers = null);
public sealed record ReferenceRatingBoard(string Key, string Name, string? Description, bool Retired = false);
public sealed record ReferenceRating(string Board, string Code, string Name, string? Description, ReferenceAsset? Icon, string? Designation = null, int? MinimumAge = null, bool Retired = false);

public sealed record ReferenceNamed(string Key, string Name, string? Description, bool Retired = false);
public sealed record ReferenceTaxonomy(string Key, string Name, string? Description, int SortOrder, string? LanguageCode, bool Retired = false);
