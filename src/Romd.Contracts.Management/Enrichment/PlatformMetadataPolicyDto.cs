namespace Romd.Contracts.Management.Enrichment;

public sealed record PlatformMetadataPolicyDto(
    string Revision,
    IReadOnlyDictionary<string, string> Defaults,
    IReadOnlyList<string> GlobalSourcePriority,
    int AffectedTitles);
