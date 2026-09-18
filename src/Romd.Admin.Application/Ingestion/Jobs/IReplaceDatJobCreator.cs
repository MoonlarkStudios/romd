namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     Creates and enqueues DAT replacement jobs.
/// </summary>
public interface IReplaceDatJobCreator
{
    /// <summary>
    ///     Creates a new DAT replacement job.
    /// </summary>
    /// <param name="existingDatId">The ID of the existing DAT to replace.</param>
    /// <param name="fileStream">The new DAT file stream.</param>
    /// <param name="fileName">The original filename.</param>
    /// <param name="platformId">Optional platform ID override.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReplaceDatJobCreationResult> CreateAsync(
        int existingDatId,
        Stream fileStream,
        string fileName,
        int? platformId,
        CancellationToken ct = default);
}

/// <summary>
///     Result of creating a DAT replacement job.
/// </summary>
public sealed record ReplaceDatJobCreationResult(
    Guid JobId,
    string BackgroundJobId,
    string StatusUrl);
