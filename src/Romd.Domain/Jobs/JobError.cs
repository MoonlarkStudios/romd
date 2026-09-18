namespace Romd.Domain.Jobs;

/// <summary>
///     Represents an error that occurred during job processing.
///     Stored as a JSON collection on the parent job entity.
/// </summary>
public sealed record JobError
{
    /// <summary>
    ///     EF Core constructor.
    /// </summary>
    private JobError()
    {
    }

    /// <summary>
    ///     Creates a new job error.
    /// </summary>
    public JobError(
        string item,
        string message,
        DateTimeOffset occurredAt,
        int? entityId = null,
        JobErrorReason reason = JobErrorReason.Unknown)
    {
        Item = item;
        Message = message;
        OccurredAt = occurredAt;
        EntityId = entityId;
        Reason = reason;
    }

    /// <summary>
    ///     The item (filename, path, etc.) that caused the error.
    /// </summary>
    public string Item { get; private set; } = null!;

    /// <summary>
    ///     The error message.
    /// </summary>
    public string Message { get; private set; } = null!;

    /// <summary>
    ///     When the error occurred.
    /// </summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>
    ///     Internal ID of the entity that failed (e.g. a title), when applicable.
    ///     Server-side only — used to scope a failure retry; never exposed in the API.
    /// </summary>
    public int? EntityId { get; private set; }

    /// <summary>
    ///     Why the item failed, for UI categorisation.
    /// </summary>
    public JobErrorReason Reason { get; private set; }
}
