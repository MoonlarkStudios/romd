namespace Romd.Domain.Jobs;

public enum ArtworkImportPhase
{
    Pending = 0,
    Importing = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}
