namespace Romd.Infrastructure.Delivery;

public sealed class ConsumerDeliveryOptions
{
    public const string SectionName = "Romd:ConsumerDelivery";
    public const int MinimumSigningSecretBytes = 32;

    public int SignedUrlTtlMinutes { get; set; } = 10;

    public string SigningSecret { get; set; } = string.Empty;

    public string SigningKeyId { get; set; } = string.Empty;

    public TimeSpan SignedUrlTtl => TimeSpan.FromMinutes(SignedUrlTtlMinutes);
}
