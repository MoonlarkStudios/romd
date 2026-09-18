namespace Romd.Domain.Source.Dat;

/// <summary>
///     Represents a managed DAT file in the system.
/// </summary>
public sealed class DatFile
{
    private DatFile(
        int id,
        string name,
        string description,
        string? version,
        string? author,
        string? url,
        DatType type,
        int? platformId,
        string originalFilename,
        int fileId,
        DateTimeOffset importedAt,
        DateTimeOffset? lastUpdatedAt,
        int gameCount,
        int romCount,
        int diskCount,
        int datSourceId,
        DatFileLifecycle lifecycle,
        DateTimeOffset? supersededAt)
    {
        Id = id;
        Name = name;
        Description = description;
        Version = version;
        Author = author;
        Url = url;
        Type = type;
        PlatformId = platformId;
        OriginalFilename = originalFilename;
        FileId = fileId;
        ImportedAt = importedAt;
        LastUpdatedAt = lastUpdatedAt;
        GameCount = gameCount;
        RomCount = romCount;
        DiskCount = diskCount;
        DatSourceId = datSourceId;
        Lifecycle = lifecycle;
        SupersededAt = supersededAt;
    }

    public int Id { get; private set; }

    // From DAT Header
    public string Name { get; private set; }
    public string Description { get; private set; }
    public string? Version { get; private set; }
    public string? Author { get; private set; }
    public string? Url { get; private set; }

    // Classification
    public DatType Type { get; private set; }
    public int? PlatformId { get; private set; }

    // File Metadata
    public string OriginalFilename { get; private set; }
    public int FileId { get; private set; }
    public DateTimeOffset ImportedAt { get; private set; }
    public DateTimeOffset? LastUpdatedAt { get; private set; }

    // Statistics (denormalized for quick access)
    public int GameCount { get; private set; }
    public int RomCount { get; private set; }
    public int DiskCount { get; private set; }

    // Source identity and lifecycle
    public int DatSourceId { get; private set; }
    public DatFileLifecycle Lifecycle { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }

    /// <summary>
    ///     Creates a new DAT file entry with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name, description, or originalFilename is null or whitespace.</exception>
    public static DatFile CreateNew(
        string name,
        string description,
        DatType type,
        string originalFilename,
        int fileId,
        int? platformId = null,
        string? version = null,
        string? author = null,
        string? url = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);

        return new DatFile(
            0,
            name,
            description,
            version,
            author,
            url,
            type,
            platformId,
            originalFilename,
            fileId,
            DateTimeOffset.UtcNow,
            null,
            0,
            0,
            0,
            0,
            DatFileLifecycle.Active,
            null);
    }

    /// <summary>
    ///     Creates a replacement version for an existing source. The version starts as
    ///     <see cref="DatFileLifecycle.PendingActivation" /> and only becomes the source's
    ///     current version through <see cref="Activate" />.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name, description, or originalFilename is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when datSourceId is not positive.</exception>
    public static DatFile CreatePendingVersion(
        string name,
        string description,
        DatType type,
        string originalFilename,
        int fileId,
        int datSourceId,
        int? platformId = null,
        string? version = null,
        string? author = null,
        string? url = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(datSourceId);

        return new DatFile(
            0,
            name,
            description,
            version,
            author,
            url,
            type,
            platformId,
            originalFilename,
            fileId,
            DateTimeOffset.UtcNow,
            null,
            0,
            0,
            0,
            datSourceId,
            DatFileLifecycle.PendingActivation,
            null);
    }

    /// <summary>
    ///     Rehydrates a DAT file entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static DatFile Rehydrate(
        int id,
        string name,
        string description,
        string? version,
        string? author,
        string? url,
        DatType type,
        int? platformId,
        string originalFilename,
        int fileId,
        DateTimeOffset importedAt,
        DateTimeOffset? lastUpdatedAt,
        int gameCount,
        int romCount,
        int diskCount,
        int datSourceId,
        DatFileLifecycle lifecycle,
        DateTimeOffset? supersededAt)
    {
        return new DatFile(
            id, name, description, version, author, url, type, platformId,
            originalFilename, fileId,
            importedAt, lastUpdatedAt, gameCount, romCount, diskCount,
            datSourceId, lifecycle, supersededAt);
    }

    /// <summary>
    ///     Updates the in-memory statistics after ingestion.
    ///     Note: Actual persistence is handled by IDatRepository.UpdateCountsAsync.
    /// </summary>
    public void UpdateStatistics(int gameCount, int romCount, int diskCount)
    {
        GameCount = gameCount;
        RomCount = romCount;
        DiskCount = diskCount;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Transitions a pending replacement version to Active.
    /// </summary>
    public void Activate()
    {
        EnsureLifecycle(DatFileLifecycle.PendingActivation);
        Lifecycle = DatFileLifecycle.Active;
    }

    /// <summary>
    ///     Transitions the active version to Superseded, recording when it was replaced.
    /// </summary>
    public void Supersede(DateTimeOffset now)
    {
        EnsureLifecycle(DatFileLifecycle.Active);
        Lifecycle = DatFileLifecycle.Superseded;
        SupersededAt = now;
    }

    private void EnsureLifecycle(DatFileLifecycle expected)
    {
        if (Lifecycle != expected)
        {
            throw new InvalidOperationException(
                $"Invalid lifecycle transition: expected {expected}, was {Lifecycle}");
        }
    }
}

/// <summary>
///     Classification of DAT file source/type.
/// </summary>
public enum DatType
{
    Unknown = 0,
    NoIntro = 1,
    Redump = 2,
    Tosec = 3,
    Mame = 4,
    Custom = 99
}
