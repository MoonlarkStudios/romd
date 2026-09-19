namespace Romd.Admin.Application.Ingestion.Jobs;

public interface IUploadJobCreator
{
    Task<UploadJobCreationResult> CreateAsync(
        Stream fileStream,
        string fileName,
        UploadJobOptions options,
        CancellationToken ct = default);
}

public sealed record UploadJobOptions
{
    public Guid? RequestId { get; init; }
    public Guid? BatchId { get; init; }
    public int? PlatformId { get; init; }
    public int MaxParallelRoms { get; init; } = 4;
    public Guid? CreatedByUserId { get; init; }

    /// <summary>
    ///     When true, ROMs that don't match any DAT entry are stored as unidentified (the inbox).
    ///     When false (default), unmatched ROMs are rejected and discarded. Only honored for users
    ///     with permission to upload unidentified ROMs.
    /// </summary>
    public bool AllowUnidentified { get; init; }

    /// <summary>
    ///     When true, matched ROM files are imported as owned/playable archive content without
    ///     adding their titles to the tracked collection.
    /// </summary>
    public bool ArchiveOnly { get; init; }
    public bool TrackedOnly { get; init; }
}

public sealed record UploadJobCreationResult(
    Guid JobId,
    string BackgroundJobId,
    string StatusUrl);
