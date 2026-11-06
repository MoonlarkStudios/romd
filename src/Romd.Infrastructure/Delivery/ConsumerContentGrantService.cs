using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Microsoft.Extensions.Options;
using Romd.Application.Common.Content;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Delivery;

public sealed partial class ConsumerContentGrantService : IConsumerContentGrantIssuer, IConsumerBiosGrantIssuer, IConsumerDerivedDescriptorGrantIssuer
{
    private const string _contentGrantRoutePrefix = "/delivery/content/";
    private const string _biosGrantRoutePrefix = "/delivery/bios/";
    private const string _biosGrantTokenUse = "bios";
    private const char _tokenSeparator = '.';
    private static readonly TimeSpan _clockSkew = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

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
        if (grant.Target is null || !RomDeliveryTargets.IsValid(grant.Target))
            throw new ArgumentException("Content grants require an exact retained ROM delivery target.", nameof(grant));
        var target = grant.Target;
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
            RomId = target.RomId,
            FileId = target.FileId,
            Sha256 = target.Sha256.ToString(),
            SizeBytes = target.SizeBytes,
            Filename = target.Filename,
            Format = JsonSerializer.SerializeToElement(RomDeliveryFormats.ToWire(target.Format)),
            ContentSha256 = target.ContentSha256.ToString(),
            ContentSizeBytes = target.ContentSizeBytes,
            ArtifactSha256 = target.ArtifactSha256.ToString(),
            ArtifactSizeBytes = target.ArtifactSizeBytes,
            ArtifactEncoding = target.Encoding switch
            {
                RomRepresentationEncoding.Identity => "identity",
                RomRepresentationEncoding.Rvz => "rvz",
                RomRepresentationEncoding.Chd => "chd",
                _ => null
            },
            Md5 = target.Md5?.ToString(),
            Sha1 = target.Sha1?.ToString(),
            ReadBinding = ToPayload(target.ReadBinding)
        };

        string payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
        string signatureSegment = Base64UrlEncode(Sign(payloadSegment));
        string token = $"{payloadSegment}{_tokenSeparator}{signatureSegment}";

        return new ConsumerIssuedContentGrant($"{_contentGrantRoutePrefix}{token}", expiresAt);
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
        if (validatedGrant.ExpiresAt < now.Subtract(_clockSkew))
        {
            return ConsumerErrors.ExpiredContentGrant();
        }

        if (validatedGrant.IssuedAt > now.Add(_clockSkew)
            || validatedGrant.ExpiresAt <= validatedGrant.IssuedAt
            || validatedGrant.ExpiresAt - validatedGrant.IssuedAt > _options.SignedUrlTtl.Add(_clockSkew))
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
            TokenUse = _biosGrantTokenUse,
            LibraryId = grant.LibraryId,
            BiosId = grant.BiosId,
            FileId = grant.FileId,
            Sha256 = grant.Sha256.ToString(),
            SizeBytes = grant.SizeBytes
        };

        string payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
        string signatureSegment = Base64UrlEncode(Sign(payloadSegment));
        string token = $"{payloadSegment}{_tokenSeparator}{signatureSegment}";

        return new ConsumerIssuedContentGrant($"{_biosGrantRoutePrefix}{token}", expiresAt);
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
        if (validatedGrant.ExpiresAt < now.Subtract(_clockSkew))
        {
            return ConsumerErrors.ExpiredContentGrant();
        }

        if (validatedGrant.IssuedAt > now.Add(_clockSkew)
            || validatedGrant.ExpiresAt <= validatedGrant.IssuedAt
            || validatedGrant.ExpiresAt - validatedGrant.IssuedAt > _options.SignedUrlTtl.Add(_clockSkew))
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

        int separatorIndex = token.IndexOf(_tokenSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex != token.LastIndexOf(_tokenSeparator))
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
            payload = JsonSerializer.Deserialize<ContentGrantTokenPayload>(payloadBytes, _jsonOptions)
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
            payload = JsonSerializer.Deserialize<BiosGrantTokenPayload>(payloadBytes, _jsonOptions)
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

        if (!string.Equals(payload.TokenUse, _biosGrantTokenUse, StringComparison.Ordinal)
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

        var parsedFormat = RomDeliveryFormats.Parse(payload.Format.ValueKind == JsonValueKind.String ? payload.Format.GetString() : null);
        if (parsedFormat.IsError)
            return false;
        RomDeliveryTarget? target = null;
        {
            if (payload.Format.ValueKind == JsonValueKind.Undefined
                || !Sha256.TryParse(payload.ContentSha256, out var contentSha256)
                || !Sha256.TryParse(payload.ArtifactSha256, out var artifactSha256)
                || payload.ContentSizeBytes is not >= 0 || payload.ArtifactSizeBytes is not >= 0)
                return false;
            RomRepresentationEncoding? encoding = payload.ArtifactEncoding switch
            {
                "identity" => RomRepresentationEncoding.Identity,
                "rvz" => RomRepresentationEncoding.Rvz,
                "chd" => RomRepresentationEncoding.Chd,
                _ => null
            };
            if (encoding is null || payload.Md5 is not null && !Md5.TryParse(payload.Md5, out _)
                || payload.Sha1 is not null && !Sha1.TryParse(payload.Sha1, out _))
                return false;
            RomReadBinding? binding = null;
            if (encoding is RomRepresentationEncoding.Chd or RomRepresentationEncoding.Rvz)
            {
                if (!TryMapReadBinding(payload.ReadBinding, out binding))
                    return false;
            }
            else if (payload.ReadBinding is not null)
                return false;
            target = new(payload.RomId.Value, payload.FileId.Value, parsedFormat.Value, payload.Filename ?? string.Empty,
                sha256, payload.SizeBytes.Value, contentSha256, payload.ContentSizeBytes.Value,
                artifactSha256, payload.ArtifactSizeBytes.Value, encoding.Value,
                payload.Md5 is null ? null : Md5.Parse(payload.Md5), payload.Sha1 is null ? null : Sha1.Parse(payload.Sha1), binding);
            if (!RomDeliveryTargets.IsValid(target))
                return false;
        }
        if (target is null)
            return false;
        var grant = new ConsumerContentGrant(userId, payload.LibraryId.Value, payload.TitleId.Value, payload.ReleaseId.Value, target);

        validatedGrant = new ConsumerValidatedContentGrant(
            grant,
            payload.KeyId ?? string.Empty,
            grantId,
            DateTimeOffset.FromUnixTimeSeconds(issuedAtUnixSeconds),
            DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds));

        return true;
    }

    private static ReadBindingTokenPayload? ToPayload(RomReadBinding? binding) => binding switch
    {
        null => null,
        RomManagedReadBinding managed => new()
        {
            Kind = "managed",
            RepresentationId = managed.RepresentationId,
            OutputId = managed.OutputId,
            MeasurementId = managed.MeasurementId,
            ManagedBuild = managed.DecoderBuild
        },
        RomChdDeliveryBinding native => new()
        {
            Kind = "whole-chd",
            RepresentationId = native.RepresentationId,
            OutputId = native.OutputId,
            MeasurementId = native.MeasurementId,
            Decoder = DecoderPayload(native.Decoder)
        },
        RomCdMemberBinding cd => new()
        {
            Kind = "cd-member",
            RepresentationId = cd.RepresentationId,
            OutputId = cd.OutputId,
            MeasurementId = cd.MeasurementId,
            Ordinal = cd.Ordinal,
            ArtifactSha256 = cd.ArtifactSha256.ToString(),
            Decoder = DecoderPayload(cd.Decoder)
        },
        _ => throw new ArgumentException("Unknown retained read binding.", nameof(binding))
    };

    private static ChdDecoderTokenPayload DecoderPayload(RomChdDecoderBuild decoder) => new()
    {
        Rid = decoder.Rid,
        UpstreamCommit = decoder.UpstreamCommit.ToString(),
        RecipeVersion = decoder.RecipeVersion
    };

    private static bool TryMapReadBinding(ReadBindingTokenPayload? payload, out RomReadBinding? binding)
    {
        binding = null;
        if (payload is not { RepresentationId: > 0, OutputId: > 0, MeasurementId: { } measurement } || measurement == Guid.Empty)
            return false;
        if (payload.Kind == "managed")
        {
            if (payload.Decoder is not null || payload.Ordinal is not null || payload.ArtifactSha256 is not null ||
                string.IsNullOrWhiteSpace(payload.ManagedBuild) || payload.ManagedBuild.Length > 256 || payload.ManagedBuild.Any(char.IsControl))
                return false;
            binding = new RomManagedReadBinding(payload.RepresentationId.Value, payload.OutputId.Value, measurement, payload.ManagedBuild);
            return true;
        }
        if (payload.ManagedBuild is not null || payload.Decoder is not { } decoder || !Sha1.TryParse(decoder.UpstreamCommit, out var pin) ||
            decoder.RecipeVersion is not { } recipe)
            return false;
        try
        {
            var build = new RomChdDecoderBuild(decoder.Rid ?? string.Empty, pin, recipe);
            if (payload.Kind == "whole-chd")
            {
                if (payload.Ordinal is not null || payload.ArtifactSha256 is not null)
                    return false;
                binding = new RomChdDeliveryBinding(payload.RepresentationId.Value, payload.OutputId.Value, measurement, build);
            }
            else if (payload.Kind == "cd-member")
            {
                if (payload.Ordinal is not (>= 0 and <= 198) || !Sha256.TryParse(payload.ArtifactSha256, out var artifact))
                    return false;
                binding = new RomCdMemberBinding(payload.RepresentationId.Value, payload.OutputId.Value, payload.Ordinal.Value, artifact, measurement, build);
            }
            else
                return false;
            return true;
        }
        catch (ArgumentException) { return false; }
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
        [JsonPropertyName("format")]
        public JsonElement Format { get; init; }
        [JsonPropertyName("content_sha256")]
        public string? ContentSha256 { get; init; }
        [JsonPropertyName("content_size")]
        public long? ContentSizeBytes { get; init; }
        [JsonPropertyName("artifact_sha256")]
        public string? ArtifactSha256 { get; init; }
        [JsonPropertyName("artifact_size")]
        public long? ArtifactSizeBytes { get; init; }
        [JsonPropertyName("artifact_encoding")]
        public string? ArtifactEncoding { get; init; }
        [JsonPropertyName("md5")]
        public string? Md5 { get; init; }
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; init; }
        [JsonPropertyName("filename")]
        public string? Filename { get; init; }
        [JsonPropertyName("read_binding")]
        public ReadBindingTokenPayload? ReadBinding { get; init; }

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

    private sealed class ReadBindingTokenPayload
    {
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("output_id")] public int? OutputId { get; init; }
        [JsonPropertyName("ordinal")] public int? Ordinal { get; init; }
        [JsonPropertyName("artifact_sha256")] public string? ArtifactSha256 { get; init; }
        [JsonPropertyName("representation_id")] public int? RepresentationId { get; init; }
        [JsonPropertyName("measurement_id")] public Guid? MeasurementId { get; init; }
        [JsonPropertyName("managed_build")] public string? ManagedBuild { get; init; }
        [JsonPropertyName("decoder")] public ChdDecoderTokenPayload? Decoder { get; init; }
    }

    private sealed class ChdDecoderTokenPayload
    {
        [JsonPropertyName("rid")] public string? Rid { get; init; }
        [JsonPropertyName("upstream_commit")] public string? UpstreamCommit { get; init; }
        [JsonPropertyName("recipe_version")] public int? RecipeVersion { get; init; }
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
