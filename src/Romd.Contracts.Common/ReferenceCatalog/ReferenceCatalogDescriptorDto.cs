namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>A bounded manifest for one complete published schema-v1 catalog. Revision validates this manifest only.</summary>
public sealed record ReferenceCatalogDescriptorDto(int SchemaVersion, int BuiltInVersion, string Revision,
    string CatalogRevision, string ContentHash, ReferenceCatalogCountsDto Counts);
public sealed record ReferenceCatalogCountsDto(int Systems, int Companies, int Regions, int Languages, int RatingBoards, int Ratings);
