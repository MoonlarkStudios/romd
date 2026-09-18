using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Microsoft.Extensions.Options;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;

namespace Romd.Infrastructure.Delivery;

public sealed class ConsumerContentGrantService : IConsumerContentGrantIssuer, IConsumerBiosGrantIssuer
{
    private const string ContentGrantRoutePrefix = "/delivery/content/";
    private const string BiosGrantRoutePrefix = "/delivery/bios/";
    private const string BiosGrantTokenUse = "bios";
    private const char TokenSeparator = '.';
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConsumerDeliveryOptions _options;
    private readonly byte[] _signingKey;
    private readonly TimeProvider _timeProvider;

    public ConsumerContentGrantService(
        IOptions<ConsumerDeliveryOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _signingKey = CreateSigningKey(_options);
        _timeProvider = timeProvider;
    }

    public ConsumerIssuedContentGrant IssueDownloadGrant(ConsumerContentGrant grant)
    {
        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt.Add(_options.SignedUrlTtl);
        var payload = new ContentGrantTokenPayload
        {
            IssuedAtUnixSeconds = issuedAt.ToUnixTimeSeconds(),
            ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            KeyId = _options.SigningKeyId,
            GrantId = Guid.NewGuid().ToString("D"),
            Subject = grant.UserId.ToString("D"),
            LibraryId = grant.LibraryId,
            TitleId = grant.TitleId,
            ReleaseId = grant.ReleaseId,
            RomId = grant.RomId,
            FileId = grant.FileId,
            Sha256 = grant.Sha256.ToString(),
            SizeBytes = grant.SizeBytes
        };

        string payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        string signatureSegment = Base64UrlEncode(Sign(payloadSegment));
        string token = $"{payloadSegment}{TokenSeparator}{signatureSegment}";

        return new ConsumerIssuedContentGrant($"{ContentGrantRoutePrefix}{token}", expiresAt);
    }

    public ErrorOr<ConsumerValidatedContentGrant> ValidateDownloadGrant(string token)
    {
        if (!TrySplitToken(token, out string payloadSegment, out string signatureSegment)
            || !TryDecodePayload(payloadSegment, out var payload))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        if (!string.Equals(payload.KeyId, _options.SigningKeyId, StringComparison.Ordinal))
        {
            return ConsumerErrors.UnknownContentGrantKey();
        }

        if (!TryBase64UrlDecode(signatureSegment, out byte[] signature)
            || signature.Length != HMACSHA256.HashSizeInBytes
            || !CryptographicOperations.FixedTimeEquals(signature, Sign(payloadSegment)))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        if (!TryMapPayload(payload, out var validatedGrant))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        var now = _timeProvider.GetUtcNow();
        if (validatedGrant.ExpiresAt < now.Subtract(ClockSkew))
        {
            return ConsumerErrors.ExpiredContentGrant();
        }

        if (validatedGrant.IssuedAt > now.Add(ClockSkew)
            || validatedGrant.ExpiresAt <= validatedGrant.IssuedAt
            || validatedGrant.ExpiresAt - validatedGrant.IssuedAt > _options.SignedUrlTtl.Add(ClockSkew))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        return validatedGrant;
    }

    public ConsumerIssuedContentGrant IssueBiosDownloadGrant(ConsumerBiosGrant grant)
    {
        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt.Add(_options.SignedUrlTtl);
        var payload = new BiosGrantTokenPayload
        {
            IssuedAtUnixSeconds = issuedAt.ToUnixTimeSeconds(),
            ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            KeyId = _options.SigningKeyId,
            GrantId = Guid.NewGuid().ToString("D"),
            Subject = grant.UserId.ToString("D"),
            TokenUse = BiosGrantTokenUse,
            LibraryId = grant.LibraryId,
            BiosId = grant.BiosId,
            FileId = grant.FileId,
            Sha256 = grant.Sha256.ToString(),
            SizeBytes = grant.SizeBytes
        };

        string payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        string signatureSegment = Base64UrlEncode(Sign(payloadSegment));
        string token = $"{payloadSegment}{TokenSeparator}{signatureSegment}";

        return new ConsumerIssuedContentGrant($"{BiosGrantRoutePrefix}{token}", expiresAt);
    }

    public ErrorOr<ConsumerValidatedBiosGrant> ValidateBiosDownloadGrant(string token)
    {
        if (!TrySplitToken(token, out string payloadSegment, out string signatureSegment)
            || !TryDecodeBiosPayload(payloadSegment, out var payload))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        if (!string.Equals(payload.KeyId, _options.SigningKeyId, StringComparison.Ordinal))
        {
            return ConsumerErrors.UnknownContentGrantKey();
        }

        if (!TryBase64UrlDecode(signatureSegment, out byte[] signature)
            || signature.Length != HMACSHA256.HashSizeInBytes
            || !CryptographicOperations.FixedTimeEquals(signature, Sign(payloadSegment)))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        if (!TryMapBiosPayload(payload, out var validatedGrant))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        var now = _timeProvider.GetUtcNow();
        if (validatedGrant.ExpiresAt < now.Subtract(ClockSkew))
        {
            return ConsumerErrors.ExpiredContentGrant();
        }

        if (validatedGrant.IssuedAt > now.Add(ClockSkew)
            || validatedGrant.ExpiresAt <= validatedGrant.IssuedAt
            || validatedGrant.ExpiresAt - validatedGrant.IssuedAt > _options.SignedUrlTtl.Add(ClockSkew))
        {
            return ConsumerErrors.InvalidContentGrant();
        }

        return validatedGrant;
    }

    private static byte[] CreateSigningKey(ConsumerDeliveryOptions options)
    {
        if (options.SignedUrlTtlMinutes <= 0)
        {
            throw new OptionsValidationException(
                nameof(ConsumerDeliveryOptions),
                typeof(ConsumerDeliveryOptions),
                ["Consumer delivery signed URL TTL must be greater than zero."]);
        }

        if (string.IsNullOrWhiteSpace(options.SigningKeyId))
        {
            throw new OptionsValidationException(
                nameof(ConsumerDeliveryOptions),
                typeof(ConsumerDeliveryOptions),
                ["Consumer delivery signing key id is required."]);
        }

        if (string.IsNullOrWhiteSpace(options.SigningSecret))
        {
            throw new OptionsValidationException(
                nameof(ConsumerDeliveryOptions),
                typeof(ConsumerDeliveryOptions),
                ["Consumer delivery signing secret is required."]);
        }

        if (Encoding.UTF8.GetByteCount(options.SigningSecret) < ConsumerDeliveryOptions.MinimumSigningSecretBytes)
        {
            throw new OptionsValidationException(
                nameof(ConsumerDeliveryOptions),
                typeof(ConsumerDeliveryOptions),
                [$"Consumer delivery signing secret must be at least {ConsumerDeliveryOptions.MinimumSigningSecretBytes} bytes."]);
        }

        return Encoding.UTF8.GetBytes(options.SigningSecret);
    }

    private byte[] Sign(string payloadSegment) =>
        HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(payloadSegment));

    private static bool TrySplitToken(
        string token,
        out string payloadSegment,
        out string signatureSegment)
    {
        payloadSegment = string.Empty;
        signatureSegment = string.Empty;

        int separatorIndex = token.IndexOf(TokenSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex != token.LastIndexOf(TokenSeparator))
        {
            return false;
        }

        payloadSegment = token[..separatorIndex];
        signatureSegment = token[(separatorIndex + 1)..];

        return payloadSegment.Length > 0 && signatureSegment.Length > 0;
    }

    private static bool TryDecodePayload(string payloadSegment, out ContentGrantTokenPayload payload)
    {
        payload = new ContentGrantTokenPayload();

        if (!TryBase64UrlDecode(payloadSegment, out byte[] payloadBytes))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<ContentGrantTokenPayload>(payloadBytes, JsonOptions)
                      ?? new ContentGrantTokenPayload();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDecodeBiosPayload(string payloadSegment, out BiosGrantTokenPayload payload)
    {
        payload = new BiosGrantTokenPayload();

        if (!TryBase64UrlDecode(payloadSegment, out byte[] payloadBytes))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<BiosGrantTokenPayload>(payloadBytes, JsonOptions)
                      ?? new BiosGrantTokenPayload();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryMapBiosPayload(
        BiosGrantTokenPayload payload,
        out ConsumerValidatedBiosGrant validatedGrant)
    {
        validatedGrant = default!;

        if (!string.Equals(payload.TokenUse, BiosGrantTokenUse, StringComparison.Ordinal)
            || payload.IssuedAtUnixSeconds is not { } issuedAtUnixSeconds
            || payload.ExpiresAtUnixSeconds is not { } expiresAtUnixSeconds
            || !Guid.TryParse(payload.GrantId, out var grantId)
            || !Guid.TryParse(payload.Subject, out var userId)
            || payload.LibraryId is not > 0
            || payload.BiosId is not > 0
            || payload.FileId is not > 0
            || !Sha256.TryParse(payload.Sha256, out var sha256)
            || payload.SizeBytes is not >= 0)
        {
            return false;
        }

        var grant = new ConsumerBiosGrant(
            userId,
            payload.LibraryId.Value,
            payload.BiosId.Value,
            payload.FileId.Value,
            sha256,
            payload.SizeBytes.Value);

        validatedGrant = new ConsumerValidatedBiosGrant(
            grant,
            payload.KeyId ?? string.Empty,
            grantId,
            DateTimeOffset.FromUnixTimeSeconds(issuedAtUnixSeconds),
            DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds));

        return true;
    }

    private static bool TryMapPayload(
        ContentGrantTokenPayload payload,
        out ConsumerValidatedContentGrant validatedGrant)
    {
        validatedGrant = default!;

        if (payload.IssuedAtUnixSeconds is not { } issuedAtUnixSeconds
            || payload.ExpiresAtUnixSeconds is not { } expiresAtUnixSeconds
            || !Guid.TryParse(payload.GrantId, out var grantId)
            || !Guid.TryParse(payload.Subject, out var userId)
            || payload.LibraryId is not > 0
            || payload.TitleId is not > 0
            || payload.ReleaseId is not > 0
            || payload.RomId is not > 0
            || payload.FileId is not > 0
            || !Sha256.TryParse(payload.Sha256, out var sha256)
            || payload.SizeBytes is not >= 0)
        {
            return false;
        }

        var grant = new ConsumerContentGrant(
            userId,
            payload.LibraryId.Value,
            payload.TitleId.Value,
            payload.ReleaseId.Value,
            payload.RomId.Value,
            payload.FileId.Value,
            sha256,
            payload.SizeBytes.Value);

        validatedGrant = new ConsumerValidatedContentGrant(
            grant,
            payload.KeyId ?? string.Empty,
            grantId,
            DateTimeOffset.FromUnixTimeSeconds(issuedAtUnixSeconds),
            DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds));

        return true;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        string base64 = value.Replace('-', '+').Replace('_', '/');

        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => $"{base64}==",
            3 => $"{base64}=",
            _ => string.Empty
        };

        if (base64.Length == 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class ContentGrantTokenPayload
    {
        [JsonPropertyName("iat")]
        public long? IssuedAtUnixSeconds { get; init; }

        [JsonPropertyName("exp")]
        public long? ExpiresAtUnixSeconds { get; init; }

        [JsonPropertyName("kid")]
        public string? KeyId { get; init; }

        [JsonPropertyName("jti")]
        public string? GrantId { get; init; }

        [JsonPropertyName("sub")]
        public string? Subject { get; init; }

        [JsonPropertyName("library_id")]
        public int? LibraryId { get; init; }

        [JsonPropertyName("title_id")]
        public int? TitleId { get; init; }

        [JsonPropertyName("release_id")]
        public int? ReleaseId { get; init; }

        [JsonPropertyName("rom_id")]
        public int? RomId { get; init; }

        [JsonPropertyName("file_id")]
        public int? FileId { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("size")]
        public long? SizeBytes { get; init; }
    }

    /// <summary>
    ///     BIOS-lane token payload. The "use" discriminator plus the bios_id claim keep this lane
    ///     disjoint from content grants: content tokens carry neither, and BIOS tokens lack the
    ///     title/release/rom claims the content lane requires — so tokens never redeem cross-lane.
    /// </summary>
    private sealed class BiosGrantTokenPayload
    {
        [JsonPropertyName("iat")]
        public long? IssuedAtUnixSeconds { get; init; }

        [JsonPropertyName("exp")]
        public long? ExpiresAtUnixSeconds { get; init; }

        [JsonPropertyName("kid")]
        public string? KeyId { get; init; }

        [JsonPropertyName("jti")]
        public string? GrantId { get; init; }

        [JsonPropertyName("sub")]
        public string? Subject { get; init; }

        [JsonPropertyName("use")]
        public string? TokenUse { get; init; }

        [JsonPropertyName("library_id")]
        public int? LibraryId { get; init; }

        [JsonPropertyName("bios_id")]
        public int? BiosId { get; init; }

        [JsonPropertyName("file_id")]
        public int? FileId { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("size")]
        public long? SizeBytes { get; init; }
    }
}
