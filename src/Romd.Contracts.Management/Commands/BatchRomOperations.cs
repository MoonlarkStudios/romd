using System.ComponentModel.DataAnnotations;

namespace Romd.Contracts.Management.Commands;

/// <summary>
///     Request to delete multiple ROM files.
/// </summary>
public sealed record BatchDeleteRomsRequest
{
    public const int MinimumRomIds = 1;
    public const int MaximumRomIds = 1000;

    /// <summary>
    ///     Between 1 and 1000 distinct public ROM IDs to delete.
    /// </summary>
    [MinLength(MinimumRomIds)]
    [MaxLength(MaximumRomIds)]
    public required IReadOnlyList<string> RomIds { get; init; }
}

/// <summary>
///     Response from batch delete operation.
/// </summary>
public sealed record BatchDeleteResponse
{
    /// <summary>
    ///     Number of ROMs successfully deleted.
    /// </summary>
    public required int DeletedCount { get; init; }

    /// <summary>
    ///     Number of ROMs that failed to delete.
    /// </summary>
    public required int FailedCount { get; init; }

    /// <summary>
    ///     Error messages for failed deletions.
    /// </summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>
///     Response from purging the unidentified ROM inbox.
/// </summary>
public sealed record PurgeUnidentifiedResponse
{
    /// <summary>
    ///     Number of unidentified ROMs removed.
    /// </summary>
    public required int DeletedCount { get; init; }

    /// <summary>
    ///     Number of now-unreferenced CAS files (rows + blobs) reclaimed.
    /// </summary>
    public required int ReclaimedFileCount { get; init; }
}
