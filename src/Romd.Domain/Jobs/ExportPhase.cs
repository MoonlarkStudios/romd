namespace Romd.Domain.Jobs;

public enum ExportPhase
{
    Pending = 0,
    Collecting = 1,
    Packaging = 2,
    Storing = 3,
    Completed = 4,
    CompletedWithErrors = 5,
    Failed = 6,
    Cancelled = 7
}
