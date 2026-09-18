namespace Romd.Domain.ReferenceData;

public sealed record RatingDefinition(string Name, string BoardKey, string Code, string? Description = null, string? AssetHash = null, bool Monochrome = false, string? Designation = null, int? MinimumAge = null, bool Retired = false);
