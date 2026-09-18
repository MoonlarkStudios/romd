namespace Romd.Storage;

/// <summary>
///     Reports progress during storage operations.
/// </summary>
public readonly record struct StoreProgress
{
    /// <summary>
    ///     Current phase of the operation.
    /// </summary>
    public required StorePhase Phase { get; init; }

    /// <summary>
    ///     Bytes processed in the current phase.
    /// </summary>
    public required long BytesProcessed { get; init; }

    /// <summary>
    ///     Total bytes to process, if known. Null for non-seekable streams.
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>
    ///     Progress percentage (0-100) if total is known, null otherwise.
    /// </summary>
    public double? PercentComplete => TotalBytes > 0
        ? Math.Min(100.0, BytesProcessed * 100.0 / TotalBytes.Value)
        : null;
}

/// <summary>
///     Phases of a store operation.
/// </summary>
public enum StorePhase
{
    /// <summary>
    ///     Computing content hash.
    /// </summary>
    Hashing,

    /// <summary>
    ///     Compressing content.
    /// </summary>
    Compressing,

    /// <summary>
    ///     Writing to storage.
    /// </summary>
    Writing,

    /// <summary>
    ///     Operation complete.
    /// </summary>
    Complete
}
