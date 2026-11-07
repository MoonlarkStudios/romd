namespace Romd.Contracts.Management.Enrichment;

public sealed record PlatformMetadataPolicy(
    string Revision,
    IReadOnlyDictionary<string, string> Defaults,
    IReadOnlyList<string> GlobalSourcePriority,
    int AffectedTitles,
    Guid? PendingJobId = null);
