namespace Romd.Contracts.Common.ReferenceCatalog;

/// <summary>Effective display facts for a system; the key does not imply runtime support.</summary>
public sealed record SystemSummary(string Key, string Name, string CompactLabel, ReferenceAsset? Icon = null);
