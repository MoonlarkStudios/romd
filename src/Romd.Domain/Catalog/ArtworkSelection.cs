namespace Romd.Domain.Catalog;

/// <summary>
/// Durable per-title/role intent. Pending requests never replace the effective pin
/// until usable files are published. Persistence must compare Revision atomically
/// when saving transitions; an in-memory check alone cannot fence concurrent writers.
/// </summary>
public sealed class ArtworkSelection
{
    private ArtworkSelection(int titleId, ArtworkRole role, long revision,
        ArtworkSelectionMode mode, int? pinnedAssetId, Guid? pendingRequestId)
    {
        TitleId = titleId;
        Role = role;
        Revision = revision;
        Mode = mode;
        PinnedAssetId = pinnedAssetId;
        PendingRequestId = pendingRequestId;
    }

    public int TitleId { get; }
    public ArtworkRole Role { get; }
    public long Revision { get; private set; }
    public ArtworkSelectionMode Mode { get; private set; }
    public int? PinnedAssetId { get; private set; }
    public Guid? PendingRequestId { get; private set; }
    public int FocalX { get; private set; } = 50;
    public int FocalY { get; private set; } = 50;

    public static ArtworkSelection CreateAutomatic(int titleId, ArtworkRole role)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(titleId);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        return new ArtworkSelection(titleId, role, 0, ArtworkSelectionMode.Automatic, null, null);
    }

    internal static ArtworkSelection Rehydrate(int titleId, ArtworkRole role, long revision,
        ArtworkSelectionMode mode, int? pinnedAssetId, Guid? pendingRequestId, int focalX = 50, int focalY = 50) =>
        new(titleId, role, revision, mode, pinnedAssetId, pendingRequestId) { FocalX = focalX, FocalY = focalY };

    public long RequestPin(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request identity is required.", nameof(requestId));
        if (PendingRequestId == requestId) return Revision;
        Revision = checked(Revision + 1);
        PendingRequestId = requestId;
        return Revision;
    }

    public bool TryPublishPin(Guid requestId, long revision, ArtworkAsset asset, int focalX = 50, int focalY = 50)
    {
        if (focalX is < 0 or > 100 || focalY is < 0 or > 100) return false;
        ArgumentNullException.ThrowIfNull(asset);
        if (PendingRequestId != requestId || Revision != revision || !asset.IsEligible ||
            asset.TitleId != TitleId || asset.Role != Role) return false;
        Mode = ArtworkSelectionMode.Pinned;
        PinnedAssetId = asset.Id;
        PendingRequestId = null;
        FocalX = focalX;
        FocalY = focalY;
        return true;
    }

    public bool TryFailPin(Guid requestId, long revision)
    {
        if (PendingRequestId != requestId || Revision != revision) return false;
        PendingRequestId = null;
        return true;
    }

    public long ReturnToAutomatic()
    {
        Revision = checked(Revision + 1);
        Mode = ArtworkSelectionMode.Automatic;
        PinnedAssetId = null;
        PendingRequestId = null;
        FocalX = FocalY = 50;
        return Revision;
    }

    public bool TryPinRetained(ArtworkAsset asset, long expectedRevision, int focalX = 50, int focalY = 50)
    {
        if (focalX is < 0 or > 100 || focalY is < 0 or > 100) return false;
        if (Revision != expectedRevision || !asset.IsEligible || asset.TitleId != TitleId || asset.Role != Role)
            return false;
        Revision = checked(Revision + 1);
        Mode = ArtworkSelectionMode.Pinned;
        PinnedAssetId = asset.Id;
        PendingRequestId = null;
        FocalX = focalX;
        FocalY = focalY;
        return true;
    }
}
