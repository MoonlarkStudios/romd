using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerReleaseManifest;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class ConsumerReleaseManifestRepositoryTests
{
    [Fact]
    public async Task GetManifestAsync_StalePreferredChdAndCurrentHeldChd_KeepsFlatOriginalUnavailableOnWire()
    {
        var preferred = await SeedPs2ChdAsync(preferred: true);
        var resident = new RomChdDecoderBuild(NativeDecoder.Rid, NativeDecoder.UpstreamCommit, NativeDecoder.RecipeVersion + 1);
        await using var db = CreateDb();
        db.Files.Add(new FileEntityPersistence
        {
            Id = 3,
            Sha256 = NewSha256(3),
            Size = 4096,
            SizeOnDisk = 4096,
            ArtifactMd5 = NewMd5(3),
            ArtifactSha1 = NewSha1(3),
            CreatedAt = DateTimeOffset.UnixEpoch
        });
        await db.SaveChangesAsync();
        var held = await ConsumerMediaFixture.PublishAsync(db, 1, 3, preferred.Container,
            new RomDecoderIdentity.Native(resident), preferred: false, DateTimeOffset.UnixEpoch);
        held.IsPreferred.ShouldBeFalse();
        preferred.Facts.Verifications.ShouldHaveSingleItem().Decoder.ShouldBe(new RomDecoderIdentity.Native(NativeDecoder));
        held.Facts.Verifications.ShouldHaveSingleItem().Decoder.ShouldBe(new RomDecoderIdentity.Native(resident));

        var policy = RomMediaPolicyComposition.CreateDeliveryPolicy();
        var repository = new ConsumerReleaseManifestRepository(db, policy, MediaTestServices.CreateContentCatalog(db),
            new DerivedDescriptorReader(db), new SyntheticChdAdmission(resident));
        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(UserId);
        var grants = Substitute.For<IConsumerContentGrantIssuer>();
        grants.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>())
            .Returns(new ConsumerIssuedContentGrant("/delivery/content/held.signature", DateTimeOffset.UtcNow.AddMinutes(1)));
        var server = Substitute.For<IServerInstanceIdentity>();
        server.InstanceId.Returns(Guid.Parse("11111111-1111-4111-8111-111111111111"));
        var result = await new IssueConsumerReleaseManifestCommandHandler(user, repository, grants,
            Substitute.For<IConsumerDerivedDescriptorGrantIssuer>(), server, policy).HandleAsync(new(ReleaseId));

        result.IsError.ShouldBeFalse();
        result.Value.IsComplete.ShouldBeTrue();
        result.Value.IsDeliveryComplete.ShouldBeFalse();
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBeFalse();
        item.Sha256.ShouldBeNull();
        item.ContentGrant.ShouldBeNull();
        var original = item.Formats.Single(format => format.Format == "original");
        original.State.ShouldBe("unsupported");
        original.ContentGrant.ShouldBeNull();
        var chd = item.Formats.Single(format => format.Format == "chd");
        chd.State.ShouldBe("available");
        chd.Sha256.ShouldBe(NewSha256(3).ToString());
        chd.ContentGrant!.DownloadUrl.ShouldBe("/delivery/content/held.signature");
        grants.Received(1).IssueDownloadGrant(Arg.Is<ConsumerContentGrant>(grant => grant.Target.FileId == 3 && grant.Target.Format == RomDeliveryFormat.Chd));
        var wire = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } });
        var flat = wire.GetProperty("items")[0];
        flat.GetProperty("isAvailable").GetBoolean().ShouldBeFalse();
        flat.GetProperty("sha256").ValueKind.ShouldBe(JsonValueKind.Null);
        flat.GetProperty("contentGrant").ValueKind.ShouldBe(JsonValueKind.Null);
        wire.GetProperty("isDeliveryComplete").GetBoolean().ShouldBeFalse();
    }
}
