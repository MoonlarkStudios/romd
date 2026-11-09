using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Application.Common.Ids;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

using ConsumerDiscSetDisc = Romd.Consumer.Application.Delivery.ConsumerDiscSetDisc;

using ConsumerReleaseManifestItem = Romd.Consumer.Application.Delivery.ConsumerReleaseManifestItem;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed partial class IssueConsumerDiscSetManifestTests
{
    private readonly ICurrentUser user = Substitute.For<ICurrentUser>();
    private readonly IConsumerDiscSetRepository repository = Substitute.For<IConsumerDiscSetRepository>();
    private readonly IConsumerContentGrantIssuer issuer = Substitute.For<IConsumerContentGrantIssuer>();
    private readonly IServerInstanceIdentity server = Substitute.For<IServerInstanceIdentity>();
    private readonly IRomContentReader reader = Substitute.For<IRomContentReader>();
    private readonly IConsumerOriginalLayoutGrantIssuer attachmentIssuer = Substitute.For<IConsumerOriginalLayoutGrantIssuer>();
    private readonly IConsumerContentArtifactResolver artifacts = Substitute.For<IConsumerContentArtifactResolver>();
    private readonly IConsumerDerivedDescriptorGrantIssuer derivedIssuer = Substitute.For<IConsumerDerivedDescriptorGrantIssuer>();
    private readonly IConsumerDerivedDescriptorSource derivedSource = Substitute.For<IConsumerDerivedDescriptorSource>();
    private readonly ConsumerDiscSetPlan plan;
    private readonly Dictionary<int, byte[]> bytes = [];
    private readonly List<ConsumerContentGrant> grants = [];

    public IssueConsumerDiscSetManifestTests()
    {
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(Guid.NewGuid());
        server.InstanceId.Returns(Guid.NewGuid());
        plan = new(3, Guid.NewGuid(), "Game (USA)", null, [Disc(1), Disc(2)]);
        SetPlan(plan);
        issuer.IssueDownloadGrant(Arg.Any<ConsumerContentGrant>()).Returns(call =>
        { var grant = call.Arg<ConsumerContentGrant>(); grants.Add(grant); return new("/content/" + grant.Target.FileId, DateTimeOffset.UtcNow.AddMinutes(10)); });
        reader.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call => Task.FromResult<Stream?>(new MemoryStream(bytes[call.Arg<RomDeliveryTarget>().FileId])));
    }
    // Application fixtures declare a synthetic CRC; these bytes exercise SHA-256/dependency checks, not decoder qualification.
    private static RomLogicalIdentity Identity(byte[] content) => new(content.LongLength,
        Sha256.FromBytes(SHA256.HashData(content)), Sha1.FromBytes(SHA1.HashData(content)),
        Md5.FromBytes(MD5.HashData(content)), Crc32.Parse("a1b2c3d4"));
    private void SetPlan(ConsumerDiscSetPlan value) => repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>())
        .Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found(7, value));
    private ConsumerDiscSetDisc Disc(int ordinal)
    {
        var items = new List<ConsumerReleaseManifestItem>();
        foreach (string path in new[] { "game.cue", "track.bin", "game.sbi" })
        {
            int id = ordinal * 10 + items.Count;
            byte[] content = Encoding.UTF8.GetBytes(path.EndsWith(".cue") ? "FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n" : "original-" + id);
            bytes[id] = content;
            var hash = Sha256.FromBytes(SHA256.HashData(content));
            var target = new RomDeliveryTarget(id, id, RomDeliveryFormat.Original, path, hash, content.Length,
                hash, content.Length, hash, content.Length, RomRepresentationEncoding.Identity);
            items.Add(new(path, "rom", content.Length, new ConsumerReleaseManifestSource.Original(Identity(content),
                [new(path, new(RomDeliveryFormat.Original, RomDeliveryState.Available, target, "original-v1", []))])));
        }
        return new(ordinal, new(user.UserId!.Value, 5, 9, "psx", ordinal, "Game Disc " + ordinal, null, true,
            new("unknown", new("file", "game.cue"), "direct_files", items.Sum(x => x.SizeBytes)), items), "game.cue");
    }
    private Task<ErrorOr.ErrorOr<Romd.Contracts.Consumer.Releases.ConsumerDiscSetManifest>> Issue(Guid? revision = null) =>
        new IssueConsumerDiscSetManifestCommandHandler(user, repository, issuer, server, reader, attachmentIssuer, artifacts, derivedIssuer, derivedSource).HandleAsync(new(3, revision ?? plan.Revision));

    [Fact]
    public async Task HandleAsync_OriginalCueBinAndSidecar_BindsEveryMemberAndDerivedPlaylist()
    {
        var result = await Issue();
        result.IsError.ShouldBeFalse();
        result.Value.Playlist.Text.ShouldBe("disc-01/game.cue\ndisc-02/game.cue\n");
        result.Value.HasAllExpectedDiscs.ShouldBeFalse();
        result.Value.Playlist.ArtifactId.ShouldBe("m3u-sha256:" + result.Value.Playlist.Sha256);
        result.Value.Discs[1].Files.Select(x => x.RelativePath).ShouldBe(["disc-02/game.cue", "disc-02/game.sbi", "disc-02/track.bin"]);
        grants.Count.ShouldBe(6);
        grants.All(x => x.LibraryId == 7 && x.UserId == user.UserId).ShouldBeTrue();
        grants.Select(x => x.ReleaseId).Distinct().ShouldBe([1, 2]);
        grants.All(x => x.Target.Format == RomDeliveryFormat.Original).ShouldBeTrue();
    }
    [Fact]
    public async Task HandleAsync_IdenticalPlan_ReusesSnapshotIdentityDespiteNewCapabilities()
    { var first = await Issue(); var second = await Issue(); second.Value.DeliverySnapshotId.ShouldBe(first.Value.DeliverySnapshotId); }
    [Fact]
    public async Task HandleAsync_ChangedOrderOrRevision_ChangesSnapshotAndPlaylistOrder()
    {
        var first = await Issue();
        var changed = plan with { Revision = Guid.NewGuid(), Discs = plan.Discs.Reverse().ToArray() };
        SetPlan(changed);
        var second = await Issue(changed.Revision);
        second.Value.DeliverySnapshotId.ShouldNotBe(first.Value.DeliverySnapshotId);
        second.Value.Discs[0].Ordinal.ShouldBe(2);
        second.Value.HasAllExpectedDiscs.ShouldBeFalse();
    }
    [Fact]
    public async Task HandleAsync_MissingCompanion_IssuesNoPartialGrants()
    {
        var last = plan.Discs[1];
        var items = last.Release.Items.ToArray();
        items[1] = items[1] with { Source = new ConsumerReleaseManifestSource.Original(((ConsumerReleaseManifestSource.Original)items[1].Source).Identity, []) };
        SetPlan(plan with { Discs = [plan.Discs[0], last with { Release = last.Release with { Items = items } }] });
        (await Issue()).FirstError.Code.ShouldBe("DiscSet.Unavailable");
        grants.ShouldBeEmpty();
    }
    [Theory]
    [InlineData("FILE \"../track.bin\" BINARY")]
    [InlineData("FILE \"missing.bin\" BINARY")]
    public async Task HandleAsync_UnsafeOrMissingCueDependency_IssuesNoGrants(string cue)
    {
        // Keep bytes/hash consistent so failure specifically exercises dependency validation.
        var disc = plan.Discs[1];
        var items = disc.Release.Items.ToArray();
        byte[] content = Encoding.UTF8.GetBytes(cue);
        var old = items[0].Formats![0];
        var target = old.Delivery.Target!;
        var hash = Sha256.FromBytes(SHA256.HashData(content));
        target = target with { Sha256 = hash, ContentSha256 = hash, ArtifactSha256 = hash, SizeBytes = content.Length, ContentSizeBytes = content.Length, ArtifactSizeBytes = content.Length };
        bytes[target.FileId] = content;
        items[0] = items[0] with { Source = new ConsumerReleaseManifestSource.Original(Identity(content), [old with { Delivery = old.Delivery with { Target = target } }]) };
        SetPlan(plan with { Discs = [plan.Discs[0], disc with { Release = disc.Release with { Items = items } }] });
        (await Issue()).FirstError.Code.ShouldBe("DiscSet.Descriptor");
        grants.ShouldBeEmpty();
    }
    [Fact]
    public async Task HandleAsync_UnpublishedSet_IsNotFoundAndIssuesNoGrants()
    {
        repository.ReadStatusAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound(7));
        (await Issue()).FirstError.Code.ShouldBe("Consumer.ContentNotFound");
        grants.ShouldBeEmpty();
    }
    [Fact]
    public async Task HandleAsync_StaleRevisionPin_IsAConflictAndIssuesNoGrants()
    {
        SetPlan(plan with { Revision = Guid.NewGuid() });
        var result = await Issue(plan.Revision);
        result.FirstError.Code.ShouldBe("DiscSet.StaleRevision");
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        grants.ShouldBeEmpty();
    }
    private Task<ErrorOr.ErrorOr<ConsumerDiscSetManifest>> Select(ConsumerDiscSetPlan value, int[] ids, bool partial = false,
        string? required = null, string? observation = null, bool issueGrants = true)
    {
        SetPlan(value);
        var status = DiscSetContracts.Status(value, server.InstanceId);
        return new IssueConsumerDiscSetManifestCommandHandler(user, repository, issuer, server, reader, attachmentIssuer, artifacts, derivedIssuer, derivedSource).HandleAsync(
            new(3, value.Revision, new(1, value.Revision, observation ?? status.ObservationId, ids.Select(IdCoder.Encode).ToArray(), partial, required), issueGrants));
    }
    [Fact]
    public async Task Selection_UnknownTotal_DoesNotRequirePartialConsent()
    {
        var result = await Select(plan, [1, 2]);
        result.IsError.ShouldBeFalse();
        result.Value.Selection!.Kind.ShouldBe("all-known");
        result.Value.Selection.Completeness.ShouldBe("unknown-total");
        result.Value.HasAllExpectedDiscs.ShouldBeFalse();
    }
    [Fact]
    public async Task Selection_OneDiscPartial_PreservesOrdinalAndRequiresExplicitConsent()
    {
        (await Select(plan, [2])).FirstError.Code.ShouldBe("DiscSet.PartialConsent");
        grants.ShouldBeEmpty();
        var result = await Select(plan, [2], true);
        result.IsError.ShouldBeFalse();
        result.Value.Discs.Single().Ordinal.ShouldBe(2);
        result.Value.Playlist.Text.ShouldBe("disc-01/game.cue\n");
        result.Value.Selection!.OmittedDiscs.Single().ReleaseId.ShouldBe(IdCoder.Encode(1));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_KnownExpectedGap_IsPartialEvenWhenAllObservedMembersSelected(bool allow)
    {
        var value = plan with { ExpectedDiscCount = 3 };
        var result = await Select(value, [1, 2], allow);
        if (!allow)
        { result.FirstError.Code.ShouldBe("DiscSet.PartialConsent"); grants.ShouldBeEmpty(); }
        else
        { result.Value.Selection!.Completeness.ShouldBe("partial"); result.Value.Selection.MissingExpectedOrdinals.ShouldBe([3]); }
    }
    [Fact]
    public async Task Selection_ChangedObservationOrWrongOrder_IssuesNoGrants()
    {
        (await Select(plan, [1, 2], observation: "stale")).FirstError.Code.ShouldBe("DiscSet.Observation");
        (await Select(plan, [2, 1])).FirstError.Code.ShouldBe("DiscSet.Selection");
        (await Select(plan, [1, 1])).FirstError.Code.ShouldBe("DiscSet.Selection");
        grants.ShouldBeEmpty();
    }
    [Fact]
    public async Task Selection_RenewalRequiresIdenticalBytesExpectationSelectionAndRevision()
    {
        var first = await Select(plan, [1, 2]);
        grants.Clear();
        (await Select(plan, [1, 2], required: first.Value.DeliverySnapshotId)).Value.DeliverySnapshotId.ShouldBe(first.Value.DeliverySnapshotId);
        grants.Clear();
        (await Select(plan with { ExpectedDiscCount = 2 }, [1, 2], required: first.Value.DeliverySnapshotId)).FirstError.Code.ShouldBe("DiscSet.Snapshot");
        grants.ShouldBeEmpty();
        (await Select(plan, [2], true, first.Value.DeliverySnapshotId)).FirstError.Code.ShouldBe("DiscSet.Snapshot");
        grants.ShouldBeEmpty();
    }
    [Fact]
    public async Task Selection_DryRunChecksExactSnapshotWithoutIssuingCapabilities()
    {
        var issued = await Select(plan, [1, 2]);
        grants.Clear();
        var dry = await Select(plan, [1, 2], required: issued.Value.DeliverySnapshotId, issueGrants: false);
        dry.Value.DeliverySnapshotId.ShouldBe(issued.Value.DeliverySnapshotId);
        grants.ShouldBeEmpty();
    }
    [Fact]
    public async Task Selection_UnreadyOmittedMember_DoesNotBlockAuthorizedReadySubset()
    {
        var value = plan with { Discs = [plan.Discs[0], plan.Discs[1] with { Readiness = "descriptor-required" }] };
        (await Select(value, [1, 2])).FirstError.Code.ShouldBe("DiscSet.Unavailable");
        (await Select(value, [1], true)).IsError.ShouldBeFalse();
    }
    [Fact]
    public async Task Selection_RetainedCue_UsesSeparateAttachmentCapabilityAndIncludesDependencies()
    {
        var attachmentIssuer = Substitute.For<IConsumerOriginalLayoutGrantIssuer>();
        attachmentIssuer.Issue(Arg.Any<ConsumerOriginalLayoutGrant>()).Returns(new ConsumerIssuedContentGrant("/delivery/original-layout/token", DateTimeOffset.UtcNow.AddMinutes(5)));
        var resolver = Substitute.For<IConsumerContentArtifactResolver>();
        var disc = plan.Discs[0];
        var cue = disc.Release.Items[0];
        resolver.ResolveAsync(cue.Sha256!.Value, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bytes[((ConsumerReleaseManifestSource.Original)cue.Source).AvailableTarget!.FileId])));
        var layout = new OriginalDiscLayout(77, Guid.NewGuid(), 9, 1, "sha1:1", "game.cue", disc.Release.Items.Select(x =>
            new OriginalDiscLayoutFile(x.RelativePath, ((ConsumerReleaseManifestSource.Original)x.Source).AvailableTarget!.FileId, x.Sha256!.Value, x.SizeBytes,
                x.RelativePath.EndsWith(".cue") ? null : ((ConsumerReleaseManifestSource.Original)x.Source).AvailableTarget!.RomId, x.RelativePath.EndsWith(".cue") ? "descriptor" : "catalog-original")).ToArray());
        var value = plan with { Discs = [disc with { OriginalLayout = layout }, plan.Discs[1]] };
        SetPlan(value);
        var result = await new IssueConsumerDiscSetManifestCommandHandler(user, repository, issuer, server, reader, attachmentIssuer, resolver, derivedIssuer, derivedSource)
            .HandleAsync(new(3, value.Revision));
        result.IsError.ShouldBeFalse();
        result.Value.Discs[0].LayoutId.ShouldBe(IdCoder.Encode(77));
        result.Value.Discs[0].Files[0].ContentGrant.DownloadUrl.ShouldStartWith("/delivery/original-layout/");
        attachmentIssuer.Received(1).Issue(Arg.Is<ConsumerOriginalLayoutGrant>(g => g.ReleaseId == 1 && g.LayoutRevision == layout.Revision && g.FileId == ((ConsumerReleaseManifestSource.Original)cue.Source).AvailableTarget!.FileId));
        grants.Count.ShouldBe(5);
    }

}
