namespace Romd.Domain.Jobs;

/// <summary>
///     Represents the current phase of a DAT replacement job.
/// </summary>
public enum ReplaceDatPhase
{
    /// <summary>
    ///     Job created but not yet started.
    /// </summary>
    Pending = 0,

    /// <summary>
    ///     Ingesting the new DAT file.
    /// </summary>
    Ingesting = 1,

    /// <summary>
    ///     Replacing the old DAT with the new one.
    /// </summary>
    Replacing = 2,

    /// <summary>
    ///     Job completed successfully with no errors.
    /// </summary>
    Completed = 3,

    /// <summary>
    ///     Job completed but some items failed.
    /// </summary>
    CompletedWithErrors = 4,

    /// <summary>
    ///     Job failed due to a fatal error.
    /// </summary>
    Failed = 5,

    /// <summary>
    ///     Job was cancelled by user or system.
    /// </summary>
    Cancelled = 6
}
