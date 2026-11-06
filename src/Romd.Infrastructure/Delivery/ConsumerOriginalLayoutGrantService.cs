using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Options;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;

namespace Romd.Infrastructure.Delivery;

/// <summary>Disjoint signed lane for retained original descriptors. Existing ROM/BIOS tokens cannot redeem here.</summary>
public sealed class ConsumerOriginalLayoutGrantService(IOptions<ConsumerDeliveryOptions> options, TimeProvider timeProvider)
    : IConsumerOriginalLayoutGrantIssuer
{
    private readonly ConsumerDeliveryOptions _options = options.Value;
    public ConsumerIssuedContentGrant Issue(ConsumerOriginalLayoutGrant grant)
    {
        var now = timeProvider.GetUtcNow();
        var expires = now.Add(_options.SignedUrlTtl);
        var payload = new Payload("original-disc-layout-v1", _options.SigningKeyId, now.ToUnixTimeSeconds(),
            expires.ToUnixTimeSeconds(), Guid.NewGuid(), grant.UserId, grant.LibraryId, grant.TitleId, grant.ReleaseId,
            grant.LayoutId, grant.LayoutRevision, grant.FileId, grant.Sha256.ToString(), grant.SizeBytes, grant.Filename);
        string segment = Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return new("/delivery/original-layout/" + segment + "." + Encode(Sign(segment)), expires);
    }

    public ErrorOr<ConsumerValidatedOriginalLayoutGrant> Validate(string token)
    {
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length != 2 || token.Length > 8192)
                return ConsumerErrors.InvalidContentGrant();
            byte[] signature = Decode(parts[1]);
            if (signature.Length != HMACSHA256.HashSizeInBytes || !CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))
                return ConsumerErrors.InvalidContentGrant();
            var payload = JsonSerializer.Deserialize<Payload>(Decode(parts[0]));
            if (payload is null || payload.Use != "original-disc-layout-v1" || payload.KeyId != _options.SigningKeyId
                || payload.GrantId == Guid.Empty || payload.UserId == Guid.Empty || payload.LibraryId <= 0 || payload.TitleId <= 0
                || payload.ReleaseId <= 0 || payload.LayoutId <= 0 || payload.LayoutRevision == Guid.Empty || payload.FileId <= 0
                || payload.SizeBytes is < 0 or > 1_048_576 || !Sha256.TryParse(payload.Sha256, out var hash)
                || string.IsNullOrWhiteSpace(payload.Filename) || !payload.Filename.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
                || payload.Expires <= payload.Issued || payload.Expires - payload.Issued > _options.SignedUrlTtl.TotalSeconds + 30)
                return ConsumerErrors.InvalidContentGrant();
            var now = timeProvider.GetUtcNow();
            var expires = DateTimeOffset.FromUnixTimeSeconds(payload.Expires);
            if (expires < now.AddSeconds(-30))
                return ConsumerErrors.ExpiredContentGrant();
            if (DateTimeOffset.FromUnixTimeSeconds(payload.Issued) > now.AddSeconds(30))
                return ConsumerErrors.InvalidContentGrant();
            return new ConsumerValidatedOriginalLayoutGrant(new(payload.UserId, payload.LibraryId, payload.TitleId,
                payload.ReleaseId, payload.LayoutId, payload.LayoutRevision, payload.FileId, hash, payload.SizeBytes, payload.Filename), expires);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        { return ConsumerErrors.InvalidContentGrant(); }
    }
    private byte[] Sign(string segment)
    {
        if (_options.SigningSecret.Length < ConsumerDeliveryOptions.MinimumSigningSecretBytes
            || string.IsNullOrWhiteSpace(_options.SigningKeyId) || _options.SignedUrlTtlMinutes <= 0)
            throw new OptionsValidationException(nameof(ConsumerDeliveryOptions), typeof(ConsumerDeliveryOptions), ["Valid delivery signing configuration is required."]);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.SigningSecret), Encoding.UTF8.GetBytes(segment));
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
        + new string('=', (4 - value.Length % 4) % 4));
    private sealed record Payload(string Use, string KeyId, long Issued, long Expires, Guid GrantId, Guid UserId,
        int LibraryId, int TitleId, int ReleaseId, int LayoutId, Guid LayoutRevision, int FileId, string Sha256, long SizeBytes, string Filename);
}
