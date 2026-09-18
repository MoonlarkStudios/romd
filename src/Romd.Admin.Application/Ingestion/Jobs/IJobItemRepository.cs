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
///     A keyset page ordered by import timestamp and ID. The cursor identifies the last item
///     in this job; filenames can be searched without loading the complete archive ledger.
/// </summary>
public sealed record JobItemPageResult(IReadOnlyList<JobItemView> Items, bool HasMore, string? NextCursor = null);

public interface IJobItemRepository
{
    /// <summary>Persists a batch of provenance records. Called from the upload job executor.</summary>
    Task AddRangeAsync(IReadOnlyList<JobItem> items, CancellationToken ct = default);

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
        Guid? cursor = null,
        string? search = null);

    /// <summary>
    ///     Returns every provenance record for a job (used by export). Callers that expose the
    ///     resolved names must own a read transaction because resolution can require multiple
    ///     bounded database statements.
    /// </summary>
    Task<IReadOnlyList<JobItemView>> GetAllByJobAsync(Guid jobId, CancellationToken ct = default);
}
