using Microsoft.Extensions.Options;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Delivery;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Delivery;

public sealed class ConsumerBiosGrantServiceTests
{
    private const string DefaultSigningSecret = "consumer-delivery-signing-secret-at-least-32-chars";
    private static readonly DateTimeOffset Now = new(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Sha256 BiosSha256 =
        Sha256.Parse("ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100");

    [Fact]
    public void IssueBiosDownloadGrant_ValidGrant_BindsClaimsAndUsesConfiguredTtl()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var service = CreateService(timeProvider, signedUrlTtlMinutes: 7);
        var grant = CreateBiosGrant();

        var issuedGrant = service.IssueBiosDownloadGrant(grant);
        var validatedGrant = service.ValidateBiosDownloadGrant(GetToken(issuedGrant.DownloadUrl));

        issuedGrant.DownloadUrl.ShouldStartWith("/delivery/bios/");
        issuedGrant.ExpiresAt.ShouldBe(Now.AddMinutes(7));
        validatedGrant.IsError.ShouldBeFalse();
        validatedGrant.Value.Grant.ShouldBe(grant);
        validatedGrant.Value.KeyId.ShouldBe("phase2a");
        validatedGrant.Value.GrantId.ShouldNotBe(Guid.Empty);
        validatedGrant.Value.IssuedAt.ShouldBe(Now);
        validatedGrant.Value.ExpiresAt.ShouldBe(Now.AddMinutes(7));
    }

    [Fact]
    public void ValidateBiosDownloadGrant_ExpiredGrant_ReturnsExpiredGrant()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var service = CreateService(timeProvider, signedUrlTtlMinutes: 5);
        var issuedGrant = service.IssueBiosDownloadGrant(CreateBiosGrant());

        timeProvider.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(31)));
        var result = service.ValidateBiosDownloadGrant(GetToken(issuedGrant.DownloadUrl));

        result.ShouldHaveSingleError(ConsumerErrors.ExpiredContentGrant().Code);
    }

    [Fact]
    public void ValidateBiosDownloadGrant_TamperedToken_ReturnsInvalidGrant()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var issuedGrant = service.IssueBiosDownloadGrant(CreateBiosGrant());
        string token = GetToken(issuedGrant.DownloadUrl);

        var result = service.ValidateBiosDownloadGrant(ReplaceFirstSignatureCharacter(token));

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateBiosDownloadGrant_UnknownKey_ReturnsUnknownKey()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var issuer = CreateService(timeProvider, signingKeyId: "old-key");
        var validator = CreateService(timeProvider, signingKeyId: "new-key");
        var issuedGrant = issuer.IssueBiosDownloadGrant(CreateBiosGrant());

        var result = validator.ValidateBiosDownloadGrant(GetToken(issuedGrant.DownloadUrl));

        result.ShouldHaveSingleError(ConsumerErrors.UnknownContentGrantKey().Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("one.two.three")]
    [InlineData("!!.signature")]
    public void ValidateBiosDownloadGrant_MalformedGrant_ReturnsInvalidGrant(string token)
    {
        var service = CreateService(new ManualTimeProvider(Now));

        var result = service.ValidateBiosDownloadGrant(token);

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateBiosDownloadGrant_ContentGrantToken_ReturnsInvalidGrant()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var issuedContentGrant = service.IssueDownloadGrant(CreateContentGrant());

        var result = service.ValidateBiosDownloadGrant(GetToken(issuedContentGrant.DownloadUrl));

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    [Fact]
    public void ValidateDownloadGrant_BiosGrantToken_ReturnsInvalidGrant()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var issuedBiosGrant = service.IssueBiosDownloadGrant(CreateBiosGrant());

        var result = service.ValidateDownloadGrant(GetToken(issuedBiosGrant.DownloadUrl));

        result.ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }

    private static ConsumerContentGrantService CreateService(
        ManualTimeProvider timeProvider,
        int signedUrlTtlMinutes = 10,
        string signingKeyId = "phase2a",
        string signingSecret = DefaultSigningSecret) =>
        new(
            Options.Create(new ConsumerDeliveryOptions
            {
                SignedUrlTtlMinutes = signedUrlTtlMinutes,
                SigningKeyId = signingKeyId,
                SigningSecret = signingSecret
            }),
            timeProvider);

    private static ConsumerBiosGrant CreateBiosGrant() =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            LibraryId: 10,
            BiosId: 7,
            FileId: 40,
            BiosSha256,
            SizeBytes: 524_288);

    private static ConsumerContentGrant CreateContentGrant() =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            LibraryId: 10,
            TitleId: 20,
            ReleaseId: 30,
            RomId: 30,
            FileId: 40,
            BiosSha256,
            SizeBytes: 123_456);

    private static string GetToken(string downloadUrl) =>
        downloadUrl[(downloadUrl.LastIndexOf('/') + 1)..];

    private static string ReplaceFirstSignatureCharacter(string value)
    {
        int signatureStart = value.LastIndexOf('.') + 1;
        char replacement = value[signatureStart] == 'A' ? 'B' : 'A';

        return $"{value.AsSpan(..signatureStart)}{replacement}{value.AsSpan((signatureStart + 1)..)}";
    }
}
