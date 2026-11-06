namespace Romd.Domain.ReferenceData;

public sealed record RegionDefinition(string Name, int SortOrder, string? Description = null, bool Retired = false);
