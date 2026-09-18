namespace Romd.Domain.Jobs;

public enum BulkEnrichmentPhase
{
    Pending = 0,
    Enriching = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Failed = 4,
    Cancelled = 5
}
