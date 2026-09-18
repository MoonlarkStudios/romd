namespace Romd.Contracts.Management.Models;

/// <summary>
///     Response when an upload is accepted for background processing.
/// </summary>
public sealed record UploadAccepted(
    Guid JobId,
    string BackgroundJobId,
    string StatusUrl);
