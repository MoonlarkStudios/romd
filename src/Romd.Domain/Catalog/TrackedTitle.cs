namespace Romd.Domain.Catalog;

/// <summary>
///     Durable user intent to collect a catalog title. Satisfaction is derived from owned files
///     and catalog releases and is deliberately not persisted here.
/// </summary>
public sealed class TrackedTitle
{
    private TrackedTitle(
        int id,
        int titleId,
        int? pinnedCatalogReleaseId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TitleId = titleId;
        PinnedCatalogReleaseId = pinnedCatalogReleaseId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public int Id { get; }
    public int TitleId { get; }
    public int? PinnedCatalogReleaseId { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static TrackedTitle Create(int titleId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(titleId);
        return new TrackedTitle(0, titleId, null, now, now);
    }

    public void PinCatalogRelease(int catalogReleaseId, int releaseTitleId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(catalogReleaseId);
        if (releaseTitleId != TitleId)
        {
            throw new ArgumentException("A pinned catalog release must belong to the tracked title.", nameof(releaseTitleId));
        }

        PinnedCatalogReleaseId = catalogReleaseId;
        UpdatedAt = now;
    }

    public void ClearPin(DateTimeOffset now)
    {
        PinnedCatalogReleaseId = null;
        UpdatedAt = now;
    }

    internal static TrackedTitle Rehydrate(
        int id,
        int titleId,
        int? pinnedCatalogReleaseId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(id, titleId, pinnedCatalogReleaseId, createdAt, updatedAt);
}
