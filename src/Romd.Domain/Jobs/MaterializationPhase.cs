namespace Romd.Domain.Jobs;

public enum MaterializationPhase
{
    Pending = 0,
    Materializing = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Failed = 4,
    Cancelled = 5,

    /// <summary>
    ///     Terminal, non-error outcome: the run was gated by a pending catalog
    ///     projection rebuild and did no work. The library stays flagged and is
    ///     re-dispatched once the projection is clean.
    /// </summary>
    Deferred = 6
}
