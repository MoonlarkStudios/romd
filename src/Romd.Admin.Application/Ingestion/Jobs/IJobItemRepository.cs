using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>
///     A title a ROM matched, resolved to its display name at read time.
/// </summary>
public sealed record JobItemTitle(int TitleId, string Name);

/// <summary>
///     A per-file provenance record with its title/platform ids resolved to names. The write side
///     stores only the id snapshot; this is the denormalized read projection for the ledger.
/// </summary>
public sealed record JobItemView
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required JobItemKind Kind { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required JobItemOutcome Outcome { get; init; }
    public int? RomFileId { get; init; }
    public int? DatFileId { get; init; }
    public int? PlatformId { get; init; }
    public string? PlatformName { get; init; }
    public required IReadOnlyList<JobItemTitle> MatchedTitles { get; init; }
    public int? GameCount { get; init; }
    public bool? ArchiveOnly { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     A stable position in the import ledger, independent of whether the anchor row still exists.
/// </summary>
public sealed record JobItemPosition(DateTimeOffset CreatedAt, Guid Id);

/// <summary>
///     A keyset page ordered by import timestamp and ID. Filenames can be searched without
///     loading the complete archive ledger.
/// </summary>
public sealed record JobItemPageResult(IReadOnlyList<JobItemView> Items, bool HasMore, JobItemPosition? NextPosition = null);

public interface IJobItemRepository
{
    /// <summary>
    /// Stages provenance in the caller's owned execution transaction; replaying item IDs is idempotent.
    /// Every item must belong to its matching execution fence. The caller flushes and commits.
    /// </summary>
    Task AddRangeStagedAsync(IReadOnlyList<JobItem> items, CancellationToken ct = default);

    /// <summary>Stages inside an existing content transaction and matching execution fence; the caller flushes/commits.</summary>
    Task AddStagedAsync(JobItem item, CancellationToken ct = default);

    /// <summary>
    /// Looks up the versioned journal key for an exact workspace member, without display names.
    /// Legacy outcome IDs are recovered through GetOutcomesAsync before per-file execution.
    /// </summary>
    Task<JobItem?> FindOutcomeAsync(Guid jobId, JobItemKind kind, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Reads the complete, unresolved outcome ledger for execution recovery. Unlike a display
    /// or export query this must not truncate records or discard unmatched title identities.
    /// </summary>
    Task<IReadOnlyList<JobItem>> GetOutcomesAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="limit" /> provenance records for a job in import order,
    ///     optionally filtered by outcome, with title/platform names resolved. Callers that expose
    ///     the resolved names must own a read transaction because resolution can require multiple
    ///     bounded database statements.
    /// </summary>
    Task<JobItemPageResult> GetByJobAsync(
        Guid jobId,
        JobItemOutcome? outcome,
        int limit,
        CancellationToken ct = default,
        JobItemPosition? cursor = null,
        string? search = null);

    /// <summary>
    ///     Returns the historical capped resolved ledger used by internal source cleanup. Complete
    ///     public exports must traverse bounded GetByJobAsync pages instead. Callers that expose the
    ///     resolved names must own a read transaction because resolution can require multiple
    ///     bounded database statements.
    /// </summary>
    Task<IReadOnlyList<JobItemView>> GetAllByJobAsync(Guid jobId, CancellationToken ct = default);
}
