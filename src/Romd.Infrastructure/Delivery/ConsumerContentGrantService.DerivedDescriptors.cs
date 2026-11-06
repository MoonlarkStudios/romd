using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Delivery;

public sealed partial class ConsumerContentGrantService
{
    private const string _derivedDescriptorUse = "derived-descriptor-v1";
    public ConsumerIssuedContentGrant Issue(ConsumerDerivedDescriptorGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.UserId == Guid.Empty || grant.LibraryId <= 0 || grant.TitleId <= 0 || !ConsumerDerivedDescriptors.IsValid(grant.Source))
            throw new ArgumentException("Derived delivery requires an authorized user/library/title and exact captured descriptor.", nameof(grant));
        var now = _timeProvider.GetUtcNow();
        var expires = now.Add(_options.SignedUrlTtl);
        var source = grant.Source;
        var payload = new DerivedDescriptorPayload(_derivedDescriptorUse, _options.SigningKeyId, Guid.NewGuid(), grant.UserId,
            grant.LibraryId, grant.TitleId, now.ToUnixTimeSeconds(), expires.ToUnixTimeSeconds(), source.Id, source.Revision,
            source.ReleaseId, source.CatalogFileId, source.RepresentationId, source.MemberOrdinal, source.FileId, source.RelativePath,
            source.Identity.Size, source.Identity.Sha256.ToString(), source.Identity.Sha1.ToString(), source.Identity.Md5.ToString(),
            source.Identity.Crc32.ToString(), source.GeneratorProfile);
        string segment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
        return new("/delivery/derived-descriptor/" + segment + "." + Base64UrlEncode(Sign(segment)), expires);
    }
    public ErrorOr<ConsumerValidatedDerivedDescriptorGrant> Validate(string token)
    {
        try
        {
            if (token.Length > 8192 || !TrySplitToken(token, out string segment, out string signatureSegment) ||
                !TryBase64UrlDecode(signatureSegment, out byte[] signature) || signature.Length != HMACSHA256.HashSizeInBytes ||
                !CryptographicOperations.FixedTimeEquals(signature, Sign(segment)) || !TryBase64UrlDecode(segment, out byte[] bytes))
                return ConsumerErrors.InvalidContentGrant();
            var payload = JsonSerializer.Deserialize<DerivedDescriptorPayload>(bytes, _jsonOptions);
            if (payload is null || payload.Use != _derivedDescriptorUse || payload.GrantId == Guid.Empty || payload.UserId == Guid.Empty ||
                payload.LibraryId <= 0 || payload.TitleId <= 0 || !Sha256.TryParse(payload.Sha256, out var sha256) ||
                !Sha1.TryParse(payload.Sha1, out var sha1) || !Md5.TryParse(payload.Md5, out var md5) || !Crc32.TryParse(payload.Crc32, out var crc))
                return ConsumerErrors.InvalidContentGrant();
            if (payload.KeyId != _options.SigningKeyId)
                return ConsumerErrors.UnknownContentGrantKey();
            var source = new DerivedDescriptorBinding(payload.BindingId, payload.Revision, payload.ReleaseId, payload.CatalogFileId, payload.RelativePath,
                payload.RepresentationId, payload.MemberOrdinal, payload.FileId, new RomLogicalIdentity(payload.SizeBytes, sha256, sha1, md5, crc), payload.GeneratorProfile);
            if (!ConsumerDerivedDescriptors.IsValid(source))
                return ConsumerErrors.InvalidContentGrant();
            var issued = DateTimeOffset.FromUnixTimeSeconds(payload.Issued);
            var expires = DateTimeOffset.FromUnixTimeSeconds(payload.Expires);
            var now = _timeProvider.GetUtcNow();
            if (expires < now.Subtract(_clockSkew))
                return ConsumerErrors.ExpiredContentGrant();
            if (issued > now.Add(_clockSkew) || expires <= issued || expires - issued > _options.SignedUrlTtl.Add(_clockSkew))
                return ConsumerErrors.InvalidContentGrant();
            return new ConsumerValidatedDerivedDescriptorGrant(new(payload.UserId, payload.LibraryId, payload.TitleId, source),
                payload.KeyId, payload.GrantId, issued, expires);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or OverflowException)
        { return ConsumerErrors.InvalidContentGrant(); }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record DerivedDescriptorPayload(string Use, [property: JsonPropertyName("kid")] string KeyId,
        [property: JsonPropertyName("jti")] Guid GrantId, [property: JsonPropertyName("sub")] Guid UserId, int LibraryId, int TitleId,
        [property: JsonPropertyName("iat")] long Issued, [property: JsonPropertyName("exp")] long Expires, int BindingId, Guid Revision, int ReleaseId, int CatalogFileId, int RepresentationId,
        int MemberOrdinal, int FileId, string RelativePath, long SizeBytes, string Sha256, string Sha1, string Md5, string Crc32, string GeneratorProfile);
}
