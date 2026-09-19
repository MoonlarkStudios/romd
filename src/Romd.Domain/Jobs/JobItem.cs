namespace Romd.Domain.Jobs;

/// <summary>
///     Whether a <see cref="JobItem" /> records the fate of a ROM file or a DAT catalog.
/// </summary>
public enum JobItemKind
{
    Rom = 0,
    Dat = 1
}

/// <summary>
///     The recorded outcome for a single imported file.
/// </summary>
public enum JobItemOutcome
{
    /// <summary>ROM stored as a new file.</summary>
    Ingested = 0,

    /// <summary>ROM already existed (linked to the existing file).</summary>
    Deduplicated = 1,

    /// <summary>ROM was excluded by the import policy and was not stored.</summary>
    Rejected = 2,

    /// <summary>File could not be processed.</summary>
    Failed = 3,

    /// <summary>DAT catalog imported and routed to a platform.</summary>
    DatRouted = 4,

    /// <summary>DAT catalog imported but not yet routed to a platform.</summary>
    DatUnrouted = 5
}

/// <summary>
///     A write-once record of what happened to one file during an upload job — the per-file
///     provenance behind a job's aggregate counters. Stores only the minimal snapshot captured at
///     ingest time (already-computed matched title/platform ids, never re-derived names); the read
///     side resolves names by joining on those ids.
/// </summary>
public sealed class JobItem
{
    private readonly List<int> _matchedTitleIds = [];

    private JobItem()
    {
    }

    private JobItem(
        Guid jobId,
        JobItemKind kind,
        string fileName,
        long sizeBytes,
        JobItemOutcome outcome,
        int? romFileId,
        int? datFileId,
        int? platformId,
        IReadOnlyList<int> matchedTitleIds,
        int? gameCount,
        string? error,
        bool? archiveOnly)
    {
        Id = Guid.NewGuid();
        JobId = jobId;
        Kind = kind;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Outcome = outcome;
        RomFileId = romFileId;
        DatFileId = datFileId;
        PlatformId = platformId;
        _matchedTitleIds.AddRange(matchedTitleIds);
        GameCount = gameCount;
        Error = error;
        ArchiveOnly = archiveOnly;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public JobItemKind Kind { get; private set; }
    public string FileName { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public JobItemOutcome Outcome { get; private set; }

    /// <summary>The stored ROM file (for an ingest) or the existing file it deduplicated to.</summary>
    public int? RomFileId { get; private set; }

    /// <summary>The DAT file created by this catalog import.</summary>
    public int? DatFileId { get; private set; }

    /// <summary>The matched platform (ROM) or routed platform (DAT), if any.</summary>
    public int? PlatformId { get; private set; }

    /// <summary>Titles this ROM matched at import time (a ROM can match several).</summary>
    public IReadOnlyList<int> MatchedTitleIds => _matchedTitleIds;

    /// <summary>Number of games in an imported DAT catalog.</summary>
    public int? GameCount { get; private set; }

    /// <summary>
    ///     The upload mode requested for this ROM: true for archive-only mode and false for
    ///     collection mode. Null for DAT items and historical rows created before this provenance
    ///     field existed.
    /// </summary>
    public bool? ArchiveOnly { get; private set; }

    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Rehydrates a record from persistence.</summary>
    public static JobItem Rehydrate(
        Guid id,
        Guid jobId,
        JobItemKind kind,
        string fileName,
        long sizeBytes,
        JobItemOutcome outcome,
        int? romFileId,
        int? datFileId,
        int? platformId,
        IReadOnlyList<int> matchedTitleIds,
        int? gameCount,
        string? error,
        bool? archiveOnly,
        DateTimeOffset createdAt)
    {
        var item = new JobItem
        {
            Id = id,
            JobId = jobId,
            Kind = kind,
            FileName = fileName,
            SizeBytes = sizeBytes,
            Outcome = outcome,
            RomFileId = romFileId,
            DatFileId = datFileId,
            PlatformId = platformId,
            GameCount = gameCount,
            Error = error,
            ArchiveOnly = archiveOnly,
            CreatedAt = createdAt
        };
        item._matchedTitleIds.AddRange(matchedTitleIds);
        return item;
    }

    public static JobItem ForRom(
        Guid jobId,
        string fileName,
        long sizeBytes,
        RomIngestOutcome outcome,
        int? romFileId,
        IReadOnlyList<int>? matchedTitleIds,
        int? platformId,
        string? error,
        bool archiveOnly = false)
    {
        var mapped = outcome switch
        {
            RomIngestOutcome.Ingested => JobItemOutcome.Ingested,
            RomIngestOutcome.Deduplicated => JobItemOutcome.Deduplicated,
            RomIngestOutcome.Rejected => JobItemOutcome.Rejected,
            _ => JobItemOutcome.Failed
        };

        return new JobItem(
            jobId, JobItemKind.Rom, fileName, sizeBytes, mapped,
            romFileId, datFileId: null, platformId, matchedTitleIds ?? [], gameCount: null, error,
            archiveOnly);
    }

    public static JobItem ForDat(
        Guid jobId,
        string fileName,
        long sizeBytes,
        bool success,
        bool routed,
        int? platformId,
        int? datFileId,
        int? gameCount,
        string? error)
    {
        var outcome = !success
            ? JobItemOutcome.Failed
            : routed
                ? JobItemOutcome.DatRouted
                : JobItemOutcome.DatUnrouted;

        return new JobItem(
            jobId, JobItemKind.Dat, fileName, sizeBytes, outcome,
            romFileId: null, datFileId, platformId, matchedTitleIds: [], gameCount, error,
            archiveOnly: null);
    }
}
