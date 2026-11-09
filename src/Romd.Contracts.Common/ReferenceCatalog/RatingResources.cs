namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record RatingResource(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, string Board, string Code, string? Designation, int? MinimumAge, ReferenceAsset? Icon, bool Retired);
