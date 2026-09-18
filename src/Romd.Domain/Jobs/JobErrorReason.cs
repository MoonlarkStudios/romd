namespace Romd.Domain.Jobs;

/// <summary>
///     Categorises why a job item failed, so the UI can suggest the right
///     response (e.g. a provider error is worth retrying; a processing error
///     usually needs investigation). Unknown is the default for legacy errors
///     and job-level failures.
/// </summary>
public enum JobErrorReason
{
    Unknown = 0,
    ProviderError,
    ProcessingError,
}
