namespace Romd.Domain.Catalog;

/// <summary>
/// A retained immutable content revision. IsEligible is true only when the original
/// and mandatory delivery variants are durable and usable. Provider availability
/// does not determine eligibility of already retained files.
/// </summary>
public sealed class ArtworkAsset
{
    public ArtworkAsset(
        int id,
        int titleId,
        ArtworkRole role,
        string sourceId,
        string? providerGameId,
        string? providerAssetId,
        ArtworkVariant original,
        IEnumerable<ArtworkVariant> variants,
        bool isEligible,
        DateTimeOffset createdAt,
        string? attribution = null,
        string? sourcePageUrl = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(titleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(variants);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        var deliveryVariants = variants.ToArray();
        if (isEligible && deliveryVariants.Length == 0)
            throw new ArgumentException("Eligible artwork requires a delivery variant.", nameof(variants));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { original.Name };
        foreach (var variant in deliveryVariants)
        {
            if (variant is null)
                throw new ArgumentException("Delivery variants cannot contain null entries.", nameof(variants));
            if (!names.Add(variant.Name))
                throw new ArgumentException("Original and delivery variant names must be unique.", nameof(variants));
        }
        Id = id;
        TitleId = titleId;
        Role = role;
        SourceId = sourceId;
        ProviderGameId = providerGameId;
        ProviderAssetId = providerAssetId;
        Original = original;
        Variants = Array.AsReadOnly(deliveryVariants);
        IsEligible = isEligible;
        CreatedAt = createdAt;
        Attribution = attribution;
        SourcePageUrl = sourcePageUrl;
    }

    public int Id { get; }
    public int TitleId { get; }
    public ArtworkRole Role { get; }
    public string SourceId { get; }
    public string? ProviderGameId { get; }
    public string? ProviderAssetId { get; }
    public ArtworkVariant Original { get; }
    public IReadOnlyList<ArtworkVariant> Variants { get; }
    public bool IsEligible { get; }
    public DateTimeOffset CreatedAt { get; }
    public string? Attribution { get; }
    public string? SourcePageUrl { get; }
}
