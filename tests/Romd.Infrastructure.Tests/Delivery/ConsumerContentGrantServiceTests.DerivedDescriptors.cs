using System.Text.Json;
using Romd.Application.Common.Content;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Delivery;

public sealed partial class ConsumerContentGrantServiceTests
{
    private static ConsumerDerivedDescriptorGrant DerivedGrant()
    {
        var identity = MediaFactFixtures.Identity("FILE \"track.bin\" BINARY\n TRACK 01 MODE1/2352\n INDEX 01 00:00:00\n"u8);
        return new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 10, 20,
            new DerivedDescriptorBinding(7, Guid.Parse("22222222-2222-2222-2222-222222222222"), 30, 13, "disc.cue", 42, 1, 51, identity, "fixture-generator/v1"));
    }
    [Fact]
    public void DerivedGrant_RoundTrip_CapturesFullScopeAndActualIdentityWithoutRomClaims()
    {
        var service = CreateService(new ManualTimeProvider(Now), signedUrlTtlMinutes: 7);
        var grant = DerivedGrant();
        var issued = service.Issue(grant);
        issued.ExpiresAt.ShouldBe(Now.AddMinutes(7));
        issued.DownloadUrl.ShouldStartWith("/delivery/derived-descriptor/");
        var result = service.Validate(GetToken(issued.DownloadUrl));
        result.IsError.ShouldBeFalse();
        result.Value.Grant.ShouldBe(grant);
        result.Value.IssuedAt.ShouldBe(Now);
        result.Value.ExpiresAt.ShouldBe(issued.ExpiresAt);
        result.Value.GrantId.ShouldNotBe(Guid.Empty);
        result.Value.KeyId.ShouldBe("phase2a");
        var payload = ReadPayload(issued.DownloadUrl);
        payload["rom_id"].ShouldBeNull();
        payload["romId"].ShouldBeNull();
        result.Value.Grant.Source.Identity.ShouldBe(grant.Source.Identity);
    }
    [Theory]
    [InlineData("use")]
    [InlineData("empty-user")]
    [InlineData("library")]
    [InlineData("binding")]
    [InlineData("revision")]
    [InlineData("negative-member")]
    [InlineData("oversize-member")]
    [InlineData("unsafe-path")]
    [InlineData("empty-hash")]
    [InlineData("bad-crc")]
    [InlineData("negative-size")]
    [InlineData("unknown-rom-claim")]
    [InlineData("future")]
    [InlineData("excessive-ttl")]
    public void DerivedGrant_SignedMalformedOrConfusedPayload_IsRefused(string mutation)
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var issued = service.Issue(DerivedGrant());
        var payload = ReadPayload(issued.DownloadUrl);
        switch (mutation)
        {
            case "use":
                payload["use"] = "bios";
                break;
            case "empty-user":
                payload["sub"] = Guid.Empty;
                break;
            case "library":
                payload["libraryId"] = 0;
                break;
            case "binding":
                payload["bindingId"] = 0;
                break;
            case "revision":
                payload["revision"] = Guid.Empty;
                break;
            case "negative-member":
                payload["memberOrdinal"] = -1;
                break;
            case "oversize-member":
                payload["memberOrdinal"] = 199;
                break;
            case "unsafe-path":
                payload["relativePath"] = "../disc.cue";
                break;
            case "empty-hash":
                payload["sha256"] = new string('0', 64);
                break;
            case "bad-crc":
                payload["crc32"] = "not-a-crc";
                break;
            case "negative-size":
                payload["sizeBytes"] = -1;
                break;
            case "unknown-rom-claim":
                payload["romId"] = 1;
                break;
            case "future":
                payload["iat"] = Now.AddMinutes(2).ToUnixTimeSeconds();
                break;
            case "excessive-ttl":
                payload["exp"] = Now.AddHours(1).ToUnixTimeSeconds();
                break;
        }
        service.Validate(CreateSignedToken(payload.ToJsonString())).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }
    [Fact]
    public void DerivedGrant_ChangedSignatureOrKeyAndExpiry_RespectCapabilityBoundaries()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var issued = service.Issue(DerivedGrant());
        service.Validate(GetToken(ReplaceFirstSignatureCharacter(issued.DownloadUrl))).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
        var payload = ReadPayload(issued.DownloadUrl);
        payload["kid"] = "other-key";
        service.Validate(CreateSignedToken(payload.ToJsonString())).ShouldHaveSingleError(ConsumerErrors.UnknownContentGrantKey().Code);
        CreateService(new ManualTimeProvider(Now.AddHours(1))).Validate(GetToken(issued.DownloadUrl)).ShouldHaveSingleError(ConsumerErrors.ExpiredContentGrant().Code);
    }
    [Fact]
    public void DerivedGrant_RomAndBiosCapabilities_CannotCrossRedeem()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        var derived = service.Issue(DerivedGrant());
        var rom = service.IssueDownloadGrant(CreateGrant());
        var bios = service.IssueBiosDownloadGrant(new(DerivedGrant().UserId, 10, 9, 40, ContentSha256, 100));
        service.Validate(GetToken(rom.DownloadUrl)).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
        service.Validate(GetToken(bios.DownloadUrl)).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
        service.ValidateDownloadGrant(GetToken(derived.DownloadUrl)).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
        service.ValidateBiosDownloadGrant(GetToken(derived.DownloadUrl)).ShouldHaveSingleError(ConsumerErrors.InvalidContentGrant().Code);
    }
}
