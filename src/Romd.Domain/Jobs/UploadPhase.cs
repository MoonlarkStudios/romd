namespace Romd.Domain.Jobs;

/// <summary>
///     Represents the current phase of an upload job.
/// </summary>
public enum UploadPhase
{
    /// <summary>
    ///     Job created but not yet started.
    /// </summary>
    Pending = 0,

    /// <summary>
    ///     Extracting nested archives.
    /// </summary>
    Extracting = 1,

    /// <summary>
    ///     Classifying files as DATs or ROMs.
    /// </summary>
    Classifying = 2,

    /// <summary>
    ///     Processing and importing DAT files.
    /// </summary>
    IngestingDats = 3,

    /// <summary>
    ///     Processing and importing ROM files.
    /// </summary>
    IngestingRoms = 4,

    /// <summary>
    ///     Job completed successfully with no errors.
    /// </summary>
    Completed = 5,

    /// <summary>
    ///     Job completed but some items failed.
    /// </summary>
    CompletedWithErrors = 6,

    /// <summary>
    ///     Job failed due to a fatal error.
    /// </summary>
    Failed = 7,

    /// <summary>
    ///     Job was cancelled by user or system.
    /// </summary>
    Cancelled = 8
}
