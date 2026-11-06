namespace Romd.Admin.Application.Source.Dat;

public sealed record DatSubscriptionCheckValues(int? DatSourceId = null, int? PlatformId = null,
    string? SystemId = null, string? ExpectedName = null, string State = "NotChecked",
    DateTimeOffset? LastCheckedAt = null, DateTimeOffset? NextCheckAt = null, int ConsecutiveCheckFailures = 0,
    string? Message = null, int? CandidateFileId = null, string? CandidateSha256 = null,
    string? ActiveSha256 = null, Guid? JobId = null);

public sealed record DatSubscriptionCheckSnapshot(int Id, string CatalogId, DatSubscriptionCheckValues Values);
public sealed record DatSubscriptionCheckPlatform(int Id, bool Retired);

/// <summary>Bounded detached facts for enrollment/check decisions; no operation writes or flushes.</summary>
public interface IDatSubscriptionCheckReader
{
    Task<DatSubscriptionCheckSnapshot?> ReadAsync(int id, CancellationToken ct);
    /// <summary>At most two rows, in identity order, to distinguish absent, unique and ambiguous enrollment.</summary>
    Task<IReadOnlyList<DatSubscriptionCheckSnapshot>> ReadCatalogAsync(string catalogId, CancellationToken ct);
    Task<DatSubscriptionCheckSnapshot?> ReadBySourceAsync(int sourceId, CancellationToken ct);
    /// <summary>Projects the exact detached row selected for a replay without reloading its mutable state.</summary>
    Task<DatCatalogSubscription> ReadReplayStatusAsync(DatSubscriptionCheckSnapshot row, CancellationToken ct);
    /// <summary>Reads the flushed owner-staged row, retaining its in-memory timestamp precision, and current status facts.</summary>
    Task<DatCatalogSubscription> ReadWrittenStatusAsync(IStagedDatSubscriptionCheck write, CancellationToken ct);
    Task<bool> IsCheckDueAsync(int id, DateTimeOffset now, CancellationToken ct);
    Task<bool> HasRunningJobAsync(Guid? jobId, CancellationToken ct);
    Task<IReadOnlyList<int>> ReadActiveDatIdsForDocumentAsync(int fileId, int? platformId, CancellationToken ct);
    Task<IReadOnlyList<int>> ReadActiveDatIdsForNameAsync(int platformId, string name, CancellationToken ct);
    Task<DatSubscriptionCheckPlatform?> ReadPlatformDefinitionAsync(string systemKey, CancellationToken ct);
    Task<bool> IsSourceEnabledAsync(int sourceId, CancellationToken ct);
}

/// <summary>Existing advisory exclusion and row staging inside the required caller-owned transaction.</summary>
public interface IDatSubscriptionCheckStore
{
    Task<bool> TryLockCatalogStagedAsync(string catalogId, CancellationToken ct);
    Task<bool> TryLockSourceStagedAsync(int sourceId, CancellationToken ct);
    Task<IStagedDatSubscriptionCheck> StageNewAsync(string catalogId, DatSubscriptionCheckValues values, CancellationToken ct);
    Task<IStagedDatSubscriptionCheck> StageExistingAsync(int id, DatSubscriptionCheckValues values, CancellationToken ct);
}

/// <summary>A subscription write's identity, readable only after owner flush inside its original transaction.</summary>
public interface IStagedDatSubscriptionCheck
{
    int ReadIdAfterFlush();
}
