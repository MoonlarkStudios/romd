namespace Romd.Contracts.Common.ReferenceCatalog;

public sealed record RegionResource(string Key, string Ownership, int? BuiltInVersion, string Name, string? Description, int SortOrder, bool Retired);
