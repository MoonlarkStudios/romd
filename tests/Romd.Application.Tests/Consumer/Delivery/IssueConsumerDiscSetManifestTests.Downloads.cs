using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetDownload;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Source.Rom;
using Romd.Application.Tests.Content;
using Shouldly;
using Xunit;

using ConsumerDiscSetDisc = Romd.Consumer.Application.Delivery.ConsumerDiscSetDisc;
using ConsumerReleaseManifestItem = Romd.Consumer.Application.Delivery.ConsumerReleaseManifestItem;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed partial class IssueConsumerDiscSetManifestTests
{
    private async Task<ConsumerDiscSetArchive?> Download(ConsumerDiscSetPlan value, string format = "chd", bool partial = false)
    {
        ConsumerDiscSetArchive? archive = null;
        var tickets = Substitute.For<IConsumerDiscSetDownloadTickets>();
        tickets.Issue(Arg.Any<ConsumerDiscSetArchive>()).Returns(call =>
        { archive = call.Arg<ConsumerDiscSetArchive>(); return new("/delivery/disc-set/test", DateTimeOffset.UtcNow.AddMinutes(2)); });
        repository.ReadForDownloadAsync(Arg.Any<ConsumerLibraryScope>(), 3, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found(7, value));
        var handler = new IssueConsumerDiscSetDownloadCommandHandler(user, repository,
            new IssueConsumerDiscSetManifestCommandHandler(user, repository, issuer, server, reader, attachmentIssuer, artifacts, derivedIssuer, derivedSource), tickets);
        var status = DiscSetContracts.Status(value, server.InstanceId);
        var result = await handler.HandleAsync(new(3, new(1, value.Revision, status.ObservationId,
            value.Discs.Where(x => x.Readiness == "ready").Select(x => IdCoder.Encode(x.Release.ReleaseId)).ToArray(), partial), format));
        if (archive is not null)
            result.IsError.ShouldBeFalse();
        grants.ShouldBeEmpty();
        return archive;
    }

    [Fact]
    public async Task Download_ChdDiscs_ReplacesEachDiscOnceAndOrdersPlaylist()
    {
        var value = plan with { Discs = [WithCd(plan.Discs[0]), WithCd(plan.Discs[1])] };
        var archive = await Download(value);
        archive.ShouldNotBeNull();
        archive.Playlist.ShouldBe("disc-01/game.chd\ndisc-02/game.chd\n");
        archive.Entries.Select(x => x.Path).ShouldBe(["disc-01/game.chd", "disc-02/game.chd"]);
        archive.Entries.All(x => x.Source is ConsumerDiscSetArchiveSource.CdArchive).ShouldBeTrue();
        archive.PlaylistPath.ShouldBe("Game (USA).m3u");
    }

    [Fact]
    public async Task Download_ChdWithRequiredSidecar_FallsBackToCompleteOriginalLayout()
    {
        var withCd = WithCd(plan.Discs[0]);
        var value = plan with { Discs = [withCd with { Release = withCd.Release with { Items = [.. withCd.Release.Items, plan.Discs[0].Release.Items[2]] } }, WithCd(plan.Discs[1])] };
        var archive = await Download(value);
        archive.ShouldNotBeNull();
        archive.Playlist.ShouldBe("disc-01/game.cue\ndisc-02/game.chd\n");
        archive.Entries.Select(x => x.Path).ShouldContain("disc-01/game.sbi");
        archive.Entries.Count(x => x.Source is ConsumerDiscSetArchiveSource.CdArchive).ShouldBe(1);
    }

    [Fact]
    public async Task Download_OriginalChoice_PreservesCueBinAndCompanions()
    {
        var archive = await Download(plan, "original");
        archive.ShouldNotBeNull();
        archive.Entries.Count.ShouldBe(6);
        archive.Playlist.ShouldBe("disc-01/game.cue\ndisc-02/game.cue\n");
        archive.Entries.All(x => x.Source is ConsumerDiscSetArchiveSource.Original).ShouldBeTrue();
    }

    [Fact]
    public async Task Download_ChdContainsOtherBytes_DoesNotExposeTheContainer()
    {
        var disc = WithCd(plan.Discs[0]);
        var value = plan with { Discs = [plan.Discs[0] with { CdArchive = disc.CdArchive }, plan.Discs[1]] };
        var archive = await Download(value);
        archive.ShouldNotBeNull();
        archive.Entries.Any(x => x.Source is ConsumerDiscSetArchiveSource.CdArchive).ShouldBeFalse();
    }

    [Fact]
    public async Task Download_MissingExpectedDisc_RequiresConsentAndLabelsPartialArchive()
    {
        var value = plan with { ExpectedDiscCount = 3 };
        (await Download(value)).ShouldBeNull();
        var partial = (await Download(value, partial: true)).ShouldNotBeNull();
        partial.Filename.ShouldEndWith("-partial.zip");
        partial.PlaylistPath.ShouldBe("Game (USA).m3u");
    }

    [Fact]
    public async Task Download_ReservedEditionFilename_UsesPortableSetIdentityForPlaylist()
    {
        var archive = (await Download(plan with { Name = "CON" })).ShouldNotBeNull();
        archive.PlaylistPath.ShouldBe("disc-set-" + IdCoder.Encode(plan.Id) + ".m3u");
    }

    private static ConsumerDiscSetDisc WithCd(ConsumerDiscSetDisc disc)
    {
        var content = DeliveryFacts.CdContent();
        var identity = content.Identity;
        var target = new RomDeliveryTarget(disc.Ordinal * 10 + 1, disc.Ordinal * 10 + 1, RomDeliveryFormat.Original, "track.bin",
            identity.Sha256, identity.Size, identity.Sha256, identity.Size, identity.Sha256, identity.Size, RomRepresentationEncoding.Identity);
        var bin = new ConsumerReleaseManifestItem("track.bin", "rom", identity.Size,
            new ConsumerReleaseManifestSource.Original(identity, [new("track.bin", new(RomDeliveryFormat.Original, RomDeliveryState.Available, target, "original-v1", []))]));
        return disc with
        {
            Release = disc.Release with { Items = [disc.Release.Items[0], bin] },
            CdArchive = new(20 + disc.Ordinal, Guid.NewGuid(), content.Artifacts.Single().Facts, "game.chd")
        };
    }
}
