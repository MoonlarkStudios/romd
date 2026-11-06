namespace Romd.Admin.Application.Ingestion.Jobs;

public sealed record ExpiredExportArtifact(Guid JobId, string Path);

/// <summary>Expired artifact discovery and conditional metadata cleanup; file I/O belongs to the caller.</summary>
public interface IExportArtifactCleanupStore
{
    Task<IReadOnlyList<ExpiredExportArtifact>> GetExpiredArtifactsAsync(
        DateTimeOffset completedBefore,
        CancellationToken ct = default);

    /// <summary>
    ///     Clears only the still-expired terminal job's matching artifact pointer and audit time.
    ///     Requires the caller's active transaction; the caller owns flush and commit.
    /// </summary>
    Task<bool> ClearExpiredArtifactStagedAsync(
        Guid jobId,
        string expectedPath,
        DateTimeOffset completedBefore,
        DateTimeOffset clearedAt,
        CancellationToken ct = default);
}
