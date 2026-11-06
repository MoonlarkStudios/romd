namespace Romd.Domain.Jobs;

public enum EnrichmentJobPhase
{
    Pending = 0,
    Enriching = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}
