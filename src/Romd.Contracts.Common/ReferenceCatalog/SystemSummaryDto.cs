namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>Effective display facts for a system; the key does not imply runtime support.</summary>
public sealed record SystemSummaryDto(string Key, string Name, string CompactLabel, ReferenceAssetDto? Icon = null);
