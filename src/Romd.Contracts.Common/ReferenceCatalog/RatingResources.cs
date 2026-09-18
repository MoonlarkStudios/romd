namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record RatingResourceDto(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, string Board, string Code, string? Designation, int? MinimumAge, ReferenceAssetDto? Icon, bool Retired);
