namespace Romd.Admin.Application.Ingestion.Jobs;

public enum JobFilterKind { Type, Outcome, Archive, ItemOutcome, ArtworkStatus, ExportFormat }

/// <summary>The closed wire vocabulary shared by HTTP declarations and application validation.</summary>
public static class JobFilters
{
    public static readonly string[] Types = ["upload", "replace_dat", "enrichment", "bulk_enrichment", "export", "materialization", "artwork-import", "artwork-completion", "metadata-rematerialization", "media-reconciliation"];
    public static readonly string[] Outcomes = ["queued", "running", "completed", "partial", "failed", "cancelled", "deferred", "superseded", "unknown"];
    public static readonly string[] Archives = ["active", "archived", "all"];
    public static readonly string[] ItemOutcomes = ["ingested", "deduplicated", "rejected", "failed", "dat_routed", "dat_unrouted"];
    public static readonly string[] ArtworkStatuses = ["Review", "Failed", "Unfinished", "Working"];
    public static readonly string[] ExportFormats = ["csv", "json"];
    public static IReadOnlyList<string> Values(JobFilterKind kind) => kind switch
    {
        JobFilterKind.Type => Types,
        JobFilterKind.Outcome => Outcomes,
        JobFilterKind.Archive => Archives,
        JobFilterKind.ItemOutcome => ItemOutcomes,
        JobFilterKind.ArtworkStatus => ArtworkStatuses,
        JobFilterKind.ExportFormat => ExportFormats,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static bool Allows(string? value, IReadOnlyList<string> values) => value is null || values.Contains(value, StringComparer.Ordinal);
}
