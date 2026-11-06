using Microsoft.Extensions.Options;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Delivery;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Delivery;

public sealed class ConsumerOriginalLayoutGrantServiceTests
{
    private static ConsumerDeliveryOptions Options() => new() { SigningSecret = "a-separate-delivery-test-secret-long-enough", SigningKeyId = "test", SignedUrlTtlMinutes = 5 };
    private static ConsumerOriginalLayoutGrant Grant() => new(Guid.NewGuid(), 1, 2, 3, 4, Guid.NewGuid(), 5,
        Sha256.Parse(new string('a', 64)), 43, "game.cue");
    private static string Token(string url) => url[(url.LastIndexOf('/') + 1)..];
    [Fact]
    public void IssueAndValidate_BindsOwnerReleaseLayoutRevisionAndExactBytes()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new ManualTimeProvider(now);
        var service = new ConsumerOriginalLayoutGrantService(Microsoft.Extensions.Options.Options.Create(Options()), time);
        var grant = Grant();
        var issued = service.Issue(grant);
        issued.DownloadUrl.ShouldStartWith("/delivery/original-layout/");
        var validated = service.Validate(Token(issued.DownloadUrl));
        validated.IsError.ShouldBeFalse();
        validated.Value.Grant.ShouldBe(grant);
        validated.Value.ExpiresAt.ToUnixTimeSeconds().ShouldBe(now.AddMinutes(5).ToUnixTimeSeconds());
    }
    [Fact]
    public void Validate_TamperingAndExpiredTokensFail()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new ManualTimeProvider(now);
        var service = new ConsumerOriginalLayoutGrantService(Microsoft.Extensions.Options.Options.Create(Options()), time);
        string token = Token(service.Issue(Grant()).DownloadUrl);
        service.Validate("x" + token).IsError.ShouldBeTrue();
        var later = new ConsumerOriginalLayoutGrantService(Microsoft.Extensions.Options.Options.Create(Options()), new ManualTimeProvider(now.AddMinutes(6)));
        later.Validate(token).FirstError.Code.ShouldBe("Consumer.ExpiredContentGrant");
    }
    [Fact]
    public void Validate_OtherLanesCannotRedeemOrAcceptDescriptorTokens()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options());
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var descriptor = new ConsumerOriginalLayoutGrantService(options, time);
        var content = new ConsumerContentGrantService(options, time);
        var grant = Grant();
        string token = Token(descriptor.Issue(grant).DownloadUrl);
        content.ValidateDownloadGrant(token).IsError.ShouldBeTrue();
        content.ValidateBiosDownloadGrant(token).IsError.ShouldBeTrue();
        string rom = Token(content.IssueDownloadGrant(new(grant.UserId, 1, 2, 3, new(6, 5, RomDeliveryFormat.Original, "game.rom", grant.Sha256, 43, grant.Sha256, 43, grant.Sha256, 43, RomRepresentationEncoding.Identity))).DownloadUrl);
        descriptor.Validate(rom).IsError.ShouldBeTrue();
    }
    [Fact]
    public void Validate_UnknownSigningKeyOrNonDescriptorClaimsFail()
    {
        var options = Options();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new ConsumerOriginalLayoutGrantService(Microsoft.Extensions.Options.Options.Create(options), time);
        string token = Token(service.Issue(Grant()).DownloadUrl);
        var other = options;
        other.SigningKeyId = "different";
        new ConsumerOriginalLayoutGrantService(Microsoft.Extensions.Options.Options.Create(other), time).Validate(token).IsError.ShouldBeTrue();
        options.SigningKeyId = "test";
        service.Validate(Token(service.Issue(Grant() with { Filename = "track.bin" }).DownloadUrl)).IsError.ShouldBeTrue();
    }
}
