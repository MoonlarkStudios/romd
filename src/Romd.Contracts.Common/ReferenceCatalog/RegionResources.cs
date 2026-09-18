namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record RegionResourceDto(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, int SortOrder, bool Retired);
