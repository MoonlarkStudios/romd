using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using ErrorOr;
using Microsoft.Extensions.Options;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Delivery;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Delivery;

public sealed partial class ConsumerContentGrantServiceTests
{
    private const string DefaultSigningSecret = "ssssssssssssssssssssssssssssssssssssssssssssssss";
    private static readonly DateTimeOffset Now = new(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Sha256 ContentSha256 =
        Sha256.Parse("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    [Theory]
    [InlineData("round-trip")]
    [InlineData("ambiguous-proof")]
    [InlineData("whole-artifact")]
    [InlineData("bad-ordinal")]
    public void IssueDownloadGrant_CdMember_BindsOneOutputAndRejectsContainerEscalation(string mutation)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var original = CreateChdGrant(RomDeliveryFormat.Original);
        var dvd = (RomChdDeliveryBinding)original.Target!.ReadBinding!;
        var binding = new RomCdMemberBinding(42, 73, 3, original.Target.ArtifactSha256, dvd.MeasurementId, dvd.Decoder);
        var grant = original with { Target = original.Target with { ReadBinding = binding } };
        var issued = service.IssueDownloadGrant(grant);
        if (mutation == "round-trip")
        {
            service.ValidateDownloadGrant(GetToken(issued.DownloadUrl)).Value.Grant.ShouldBe(grant);
            return;
        }
        var payload = ReadPayload(issued.DownloadUrl);
        switch (mutation)
        {
            case "ambiguous-proof":
                payload["read_binding"]!["managed_build"] = "fixture-managed/v1";
                break;
            case "bad-ordinal":
                payload["read_binding"]!["ordinal"] = 199;
                break;
            case "whole-artifact":
                payload["format"] = "chd";
                payload["sha256"] = payload["artifact_sha256"]!.DeepClone();
                payload["size"] = payload["artifact_size"]!.DeepClone();
                break;
        }
        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString())).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void IssueDownloadGrant_ValidGrant_BindsClaimsAndUsesConfiguredTtl()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var service = CreateService(timeProvider, signedUrlTtlMinutes: 7);
        var grant = CreateGrant();

        var issuedGrant = service.IssueDownloadGrant(grant);
        var validatedGrant = service.ValidateDownloadGrant(GetToken(issuedGrant.DownloadUrl));

        issuedGrant.DownloadUrl.ShouldStartWith("/delivery/content/");
        issuedGrant.ExpiresAt.ShouldBe(Now.AddMinutes(7));
        validatedGrant.IsError.ShouldBeFalse();
        validatedGrant.Value.Grant.ShouldBe(grant);
        validatedGrant.Value.KeyId.ShouldBe("phase2a");
        validatedGrant.Value.GrantId.ShouldNotBe(Guid.Empty);
        validatedGrant.Value.IssuedAt.ShouldBe(Now);
        validatedGrant.Value.ExpiresAt.ShouldBe(Now.AddMinutes(7));
    }

    [Fact]
    public void IssueDownloadGrant_DefaultOptions_UsesPolicyDefaultTtl()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var service = CreateServiceWithOptions(
            timeProvider,
            new ConsumerDeliveryOptions
            {
                SigningKeyId = "phase2a",
                SigningSecret = DefaultSigningSecret
            });

        var issuedGrant = service.IssueDownloadGrant(CreateGrant());

        issuedGrant.ExpiresAt.ShouldBe(Now.AddMinutes(10));
    }

    [Fact]
    public void ValidateDownloadGrant_TamperedToken_ReturnsInvalidGrant()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var issuedGrant = service.IssueDownloadGrant(CreateGrant());
        string token = GetToken(issuedGrant.DownloadUrl);

        var result = service.ValidateDownloadGrant(ReplaceFirstSignatureCharacter(token));

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_ExpiredGrant_ReturnsExpiredGrant()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var service = CreateService(timeProvider, signedUrlTtlMinutes: 5);
        var issuedGrant = service.IssueDownloadGrant(CreateGrant());

        timeProvider.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(31)));
        var result = service.ValidateDownloadGrant(GetToken(issuedGrant.DownloadUrl));

        result.ShouldHaveSingleError(ConsumerErrors.ExpiredContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_UnknownKey_ReturnsUnknownKey()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var issuer = CreateService(timeProvider, signingKeyId: "old-key");
        var validator = CreateService(timeProvider, signingKeyId: "new-key");
        var issuedGrant = issuer.IssueDownloadGrant(CreateGrant());

        var result = validator.ValidateDownloadGrant(GetToken(issuedGrant.DownloadUrl));

        result.ShouldHaveSingleError(ConsumerErrors.UnknownContentGrantKey().Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("one.two.three")]
    [InlineData("!!.signature")]
    public void ValidateDownloadGrant_MalformedGrant_ReturnsInvalidGrant(string token)
    {
        var service = CreateService(new ManualTimeProvider(Now));

        var result = service.ValidateDownloadGrant(token);

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_SignedWrongShapeGrant_ReturnsInvalidGrant()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        string token = CreateSignedToken(
            """
            {"iat":1801396800,"exp":1801397100,"kid":"phase2a","jti":"22222222-2222-2222-2222-222222222222"}
            """);

        var result = service.ValidateDownloadGrant(token);

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_DifferentSigningSecret_ReturnsInvalidGrant()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var issuer = CreateService(timeProvider, signingSecret: "first-secret-at-least-thirty-two-characters");
        var validator = CreateService(timeProvider, signingSecret: "second-secret-at-least-thirty-two-characters");
        var issuedGrant = issuer.IssueDownloadGrant(CreateGrant());

        var result = validator.ValidateDownloadGrant(GetToken(issuedGrant.DownloadUrl));

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData("RVZ")]
    [InlineData("iso")]
    [InlineData("")]
    [InlineData(" original")]
    [InlineData("1")]
    public void ValidateDownloadGrant_SignedInvalidFormat_RejectsInsteadOfIgnoringClaim(string format)
    {
        var grant = CreateGrant();
        string token = CreateSignedToken(JsonSerializer.Serialize(new
        {
            iat = Now.ToUnixTimeSeconds(),
            exp = Now.AddMinutes(5).ToUnixTimeSeconds(),
            kid = "phase2a",
            jti = Guid.NewGuid().ToString("D"),
            sub = grant.UserId.ToString("D"),
            library_id = grant.LibraryId,
            title_id = grant.TitleId,
            release_id = grant.ReleaseId,
            rom_id = grant.Target.RomId,
            file_id = grant.Target.FileId,
            sha256 = grant.Target.Sha256.ToString(),
            size = grant.Target.SizeBytes,
            filename = "disc.iso",
            format
        }));

        var result = CreateService(new ManualTimeProvider(Now)).ValidateDownloadGrant(token);

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{}")]
    public void ValidateDownloadGrant_SignedNonStringFormat_ReturnsInvalidGrant(string claimJson)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateGrant()).DownloadUrl);
        payload["format"] = JsonNode.Parse(claimJson);
        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_FormatAbsent_RefusesIncompleteTarget()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateGrant()).DownloadUrl);
        payload.Remove("format");

        var result = service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()));

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Rvz)]
    public void IssueDownloadGrant_Target_BindsFormatAndDistinctLogicalAndArtifactFacts(RomDeliveryFormat format)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var artifact = Sha256.Parse(new string('f', 64));
        var target = new RomDeliveryTarget(30, 40, format, format == RomDeliveryFormat.Rvz ? "disc.rvz" : "disc.iso",
            format == RomDeliveryFormat.Rvz ? artifact : ContentSha256, format == RomDeliveryFormat.Rvz ? 100 : 123456,
            ContentSha256, 123456, artifact, 100, RomRepresentationEncoding.Rvz,
            Md5.Parse(new string('a', 32)), Sha1.Parse(new string('b', 40)),
            new RomManagedReadBinding(51, 61, Guid.Parse("55555555-5555-5555-5555-555555555555"), "fixture-managed/v1"));
        var grant = CreateGrant() with { Target = target };

        var issued = service.IssueDownloadGrant(grant);
        var result = service.ValidateDownloadGrant(GetToken(issued.DownloadUrl));

        result.IsError.ShouldBeFalse();
        result.Value.Grant.ShouldBe(grant);
        ReadPayload(issued.DownloadUrl)["format"]!.GetValue<string>().ShouldBe(RomDeliveryFormats.ToWire(format));
        var tampered = ReadPayload(issued.DownloadUrl);
        tampered["format"] = format == RomDeliveryFormat.Rvz ? "original" : "rvz";
        service.ValidateDownloadGrant(CreateSignedToken(tampered.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_RvzWithoutArtifactBinding_ReturnsInvalidGrant()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateGrant()).DownloadUrl);
        payload["format"] = "rvz";
        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Chd)]
    public void IssueDownloadGrant_ChdRepresentation_RoundTripsEveryRetainedBindingAndByteIdentity(RomDeliveryFormat format)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var grant = CreateChdGrant(format);

        var issued = service.IssueDownloadGrant(grant);
        var result = service.ValidateDownloadGrant(GetToken(issued.DownloadUrl));

        result.IsError.ShouldBeFalse();
        result.Value.Grant.ShouldBe(grant);
        result.Value.Grant.Target!.ReadBinding.ShouldBe(grant.Target!.ReadBinding);
        result.Value.Grant.Target.ContentSha256.ShouldBe(ContentSha256);
        result.Value.Grant.Target.ArtifactSha256.ShouldBe(Sha256.Parse(new string('f', 64)));
        ((RomChdDeliveryBinding)result.Value.Grant.Target.ReadBinding!).Decoder.ShouldBe(((RomChdDeliveryBinding)grant.Target.ReadBinding!).Decoder);
    }

    [Fact]
    public void IssueDownloadGrant_MissingRequiredTarget_RefusesUnboundArtifact()
    {
        var service = CreateService(new ManualTimeProvider(Now));

        Should.Throw<ArgumentException>(() => service.IssueDownloadGrant(CreateGrant() with { Target = null! }));
    }

    [Fact]
    public void ValidateDownloadGrant_ChdWithoutTarget_RefusesUnboundSignedArtifact()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateGrant()).DownloadUrl);
        payload["format"] = "chd";

        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData("read_binding")]
    [InlineData("read_binding/kind")]
    [InlineData("read_binding/representation_id")]
    [InlineData("read_binding/measurement_id")]
    [InlineData("read_binding/output_id")]
    [InlineData("read_binding/decoder")]
    [InlineData("read_binding/decoder/rid")]
    [InlineData("read_binding/decoder/upstream_commit")]
    [InlineData("read_binding/decoder/recipe_version")]
    [InlineData("artifact_sha256")]
    [InlineData("artifact_size")]
    [InlineData("artifact_encoding")]
    [InlineData("md5")]
    [InlineData("sha1")]
    [InlineData("format")]
    public void ValidateDownloadGrant_ReadBindingMemberMissing_RefusesInsteadOfInventingEvidence(string member)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateChdGrant()).DownloadUrl);
        string[] path = member.Split('/');
        var owner = payload;
        foreach (string segment in path[..^1])
            owner = owner[segment]!.AsObject();
        owner.Remove(path[^1]);

        var result = service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()));

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData("representation_id", "0")]
    [InlineData("measurement_id", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("output_id", "0")]
    public void ValidateDownloadGrant_ReadBindingIdentifierInvalid_Refuses(string member, string value)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateChdGrant()).DownloadUrl);
        payload["read_binding"]![member] = JsonNode.Parse(value);

        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_DecoderInThePreviousBinaryPinnedFormat_RefusesWithoutThrowing()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateChdGrant()).DownloadUrl);
        var decoder = payload["read_binding"]!["decoder"]!.AsObject();
        decoder.Remove("recipe_version");
        decoder["binary_bytes"] = 1024;
        decoder["binary_sha256"] = new string('1', 64);
        decoder["manifest_bytes"] = 512;
        decoder["manifest_sha256"] = new string('2', 64);
        decoder["source_commit"] = new string('3', 40);
        decoder["family_version"] = "1.0.0-preview.1";
        decoder["upstream_version"] = "v0.3.0";
        decoder["build_id"] = new string('4', 64);
        decoder["has_raw_sectors"] = true;
        decoder["has_subcode"] = true;
        decoder["verifies_block_crc"] = true;
        decoder["qualification"] = "legacy";

        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData("rid", "\"Linux_X64\"")]
    [InlineData("upstream_commit", "\"0000000000000000000000000000000000000000\"")]
    [InlineData("upstream_commit", "\"short\"")]
    [InlineData("recipe_version", "0")]
    [InlineData("recipe_version", "1.5")]
    public void ValidateDownloadGrant_ChdDecoderFactInvalid_Refuses(string member, string value)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateChdGrant()).DownloadUrl);
        payload["read_binding"]!["decoder"]![member] = JsonNode.Parse(value);

        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Theory]
    [InlineData("format", "\"CHD\"")]
    [InlineData("artifact_encoding", "\"identity\"")]
    [InlineData("artifact_encoding", "\"rvz\"")]
    [InlineData("md5", "\"00000000000000000000000000000000\"")]
    [InlineData("sha1", "\"0000000000000000000000000000000000000000\"")]
    public void ValidateDownloadGrant_ChdMalformedFormatOrArtifactMetadata_Refuses(string member, string value)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateChdGrant()).DownloadUrl);
        payload[member] = JsonNode.Parse(value);

        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_ChdDeliveredLogicalChecksumSubstitution_Refuses()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var payload = ReadPayload(service.IssueDownloadGrant(CreateChdGrant()).DownloadUrl);
        payload["sha256"] = ContentSha256.ToString();
        payload["size"] = 8192;

        service.ValidateDownloadGrant(CreateSignedToken(payload.ToJsonString()))
            .ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_ChdTargetTamperedWithoutSigning_Refuses()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var issued = service.IssueDownloadGrant(CreateChdGrant());
        var payload = ReadPayload(issued.DownloadUrl);
        payload["read_binding"]!["measurement_id"] = Guid.NewGuid().ToString("D");
        string originalSignature = GetToken(issued.DownloadUrl).Split('.')[1];
        string token = Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString())) + "." + originalSignature;

        service.ValidateDownloadGrant(token).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void IssueDownloadGrant_ChdReissuance_RefreshesExpiryWithoutReplacingTarget()
    {
        var time = new ManualTimeProvider(Now);
        var service = CreateService(time, signedUrlTtlMinutes: 5);
        var first = service.IssueDownloadGrant(CreateChdGrant());
        var validated = service.ValidateDownloadGrant(GetToken(first.DownloadUrl));
        validated.IsError.ShouldBeFalse();
        time.Advance(TimeSpan.FromMinutes(6));

        var renewed = service.IssueDownloadGrant(validated.Value.Grant);
        var result = service.ValidateDownloadGrant(GetToken(renewed.DownloadUrl));

        service.ValidateDownloadGrant(GetToken(first.DownloadUrl)).ShouldHaveSingleError(ConsumerErrors.ExpiredContentGrant().Code);
        result.IsError.ShouldBeFalse();
        result.Value.Grant.ShouldBe(validated.Value.Grant);
        result.Value.GrantId.ShouldNotBe(validated.Value.GrantId);
        result.Value.ExpiresAt.ShouldBe(Now.AddMinutes(11));
    }

    [Fact]
    public void IssueDownloadGrant_ChdDecoderOfAnotherRecipe_PreservesTheExactTuple()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var first = CreateChdGrant();
        var second = CreateChdGrant(recipeVersion: 2);
        ((RomChdDeliveryBinding)first.Target!.ReadBinding!).Decoder.BuildLabel.ShouldNotBe(((RomChdDeliveryBinding)second.Target!.ReadBinding!).Decoder.BuildLabel);

        var result = service.ValidateDownloadGrant(GetToken(service.IssueDownloadGrant(second).DownloadUrl));

        result.IsError.ShouldBeFalse();
        result.Value.Grant.ShouldBe(second);
        ((RomChdDeliveryBinding)result.Value.Grant.Target!.ReadBinding!).Decoder.ShouldNotBe(((RomChdDeliveryBinding)first.Target.ReadBinding!).Decoder);
    }

    private static ConsumerContentGrant CreateChdGrant(RomDeliveryFormat format = RomDeliveryFormat.Chd, int recipeVersion = 1)
    {
        var decoder = new RomChdDecoderBuild("linux-x64", Sha1.Parse(new string('4', 40)), recipeVersion);
        var binding = new RomChdDeliveryBinding(51, 61, Guid.Parse("55555555-5555-5555-5555-555555555555"), decoder);
        var artifact = Sha256.Parse(new string('f', 64));
        var target = new RomDeliveryTarget(30, 40, format, format == RomDeliveryFormat.Chd ? "disc.chd" : "disc.iso",
            format == RomDeliveryFormat.Chd ? artifact : ContentSha256, format == RomDeliveryFormat.Chd ? 4096 : 8192,
            ContentSha256, 8192, artifact, 4096, RomRepresentationEncoding.Chd,
            Md5.Parse(new string(format == RomDeliveryFormat.Chd ? 'a' : 'b', 32)),
            Sha1.Parse(new string(format == RomDeliveryFormat.Chd ? 'c' : 'd', 40)), binding);
        return CreateGrant() with { Target = target };
    }

    private static JsonObject ReadPayload(string url)
    {
        string segment = GetToken(url).Split('.')[0].Replace('-', '+').Replace('_', '/');
        return JsonNode.Parse(Convert.FromBase64String(segment.PadRight((segment.Length + 3) / 4 * 4, '=')))!.AsObject();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidTtl_ThrowsOptionsValidation(int signedUrlTtlMinutes)
    {
        var error = Should.Throw<OptionsValidationException>(() =>
            CreateService(new ManualTimeProvider(Now), signedUrlTtlMinutes: signedUrlTtlMinutes));

        error.Failures.ShouldContain("Consumer delivery signed URL TTL must be greater than zero.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_MissingSigningSecret_ThrowsOptionsValidation(string signingSecret)
    {
        var error = Should.Throw<OptionsValidationException>(() =>
            CreateService(new ManualTimeProvider(Now), signingSecret: signingSecret));

        error.Failures.ShouldContain("Consumer delivery signing secret is required.");
    }

    [Fact]
    public void Constructor_ShortSigningSecret_ThrowsOptionsValidation()
    {
        var error = Should.Throw<OptionsValidationException>(() =>
            CreateService(new ManualTimeProvider(Now), signingSecret: "short"));

        error.Failures.ShouldContain(
            $"Consumer delivery signing secret must be at least {ConsumerDeliveryOptions.MinimumSigningSecretBytes} bytes.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_MissingSigningKeyId_ThrowsOptionsValidation(string signingKeyId)
    {
        var error = Should.Throw<OptionsValidationException>(() =>
            CreateService(new ManualTimeProvider(Now), signingKeyId: signingKeyId));

        error.Failures.ShouldContain("Consumer delivery signing key id is required.");
    }

    private static ConsumerContentGrantService CreateService(
        ManualTimeProvider timeProvider,
        int signedUrlTtlMinutes = 10,
        string signingKeyId = "phase2a",
        string signingSecret = DefaultSigningSecret) =>
        CreateServiceWithOptions(
            timeProvider,
            new ConsumerDeliveryOptions
            {
                SignedUrlTtlMinutes = signedUrlTtlMinutes,
                SigningKeyId = signingKeyId,
                SigningSecret = signingSecret
            });

    private static ConsumerContentGrantService CreateServiceWithOptions(
        ManualTimeProvider timeProvider,
        ConsumerDeliveryOptions options) =>
        new(Options.Create(options), timeProvider);

    private static ConsumerContentGrant CreateGrant() =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 10, 20, 30,
            new(30, 40, RomDeliveryFormat.Original, "game.sfc", ContentSha256, 123_456, ContentSha256, 123_456, ContentSha256, 123_456, RomRepresentationEncoding.Identity));

    private static string GetToken(string downloadUrl) =>
        downloadUrl[(downloadUrl.LastIndexOf('/') + 1)..];

    private static string ReplaceFirstSignatureCharacter(string value)
    {
        int signatureStart = value.LastIndexOf('.') + 1;
        char replacement = value[signatureStart] == 'A' ? 'B' : 'A';

        return $"{value.AsSpan(..signatureStart)}{replacement}{value.AsSpan((signatureStart + 1)..)}";
    }

    private static string CreateSignedToken(string payloadJson)
    {
        string payloadSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        byte[] signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(DefaultSigningSecret),
            Encoding.UTF8.GetBytes(payloadSegment));

        return $"{payloadSegment}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal static class ErrorOrAssertions
{
    public static void ShouldHaveSingleError<TValue>(this ErrorOr<TValue> result, string code)
    {
        result.IsError.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(code);
    }
}
