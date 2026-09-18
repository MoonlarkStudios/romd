using System.Security.Cryptography;
using System.Text;
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

public sealed class ConsumerContentGrantServiceTests
{
    private const string DefaultSigningSecret = "consumer-delivery-signing-secret-at-least-32-chars";
    private static readonly DateTimeOffset Now = new(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Sha256 ContentSha256 =
        Sha256.Parse("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

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
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            LibraryId: 10,
            TitleId: 20,
            ReleaseId: 30,
            RomId: 30,
            FileId: 40,
            ContentSha256,
            SizeBytes: 123_456);

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
