namespace Romd.Application.Common.Artwork;

public interface IArtworkDelivery
{
    Task<ArtworkDelivery?> OpenAsync(int assetId, string variantName, string contentVersion,
        CancellationToken ct = default);
}

public sealed record ArtworkDelivery(Stream Content, string ContentType, string ContentHash);
