using System.Text.Json;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetDownload;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Delivery;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Catalog;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscSetDownload_PublishedChdAndOriginalDisc_BindsArchivesInsideAllMemberAuthorizationSnapshot(bool saturn)
    {
        if (saturn)
        {
            _saturn = true;
            await using var setup = database.CreateContext();
            await setup.Platforms.ExecuteUpdateAsync(row => row.SetProperty(platform => platform.CanonicalKey, "saturn").SetProperty(platform => platform.ShortName, "saturn"));
        }
        await SeedSecondReleaseAsync();
        await Publish();
        await Decide(1, "confirm");
        await Decide(2, "confirm");
        await using var db = database.CreateContext();
        var resident = Resident("admitted");
        await ConsumerArchives(db, resident.Admission);
        db.MaterializedLibraryReleases.Add(new()
        {
            LibraryId = 1,
            TitleId = 1,
            CatalogReleaseId = 2,
            DatGameId = 2,
            DatFileId = 1,
            PlatformId = 1,
            IsEligible = true,
            IsComplete = true,
            IsOwned = true,
            IsExposed = true
        });
        db.MaterializedLibraryDiscSets.Add(new()
        {
            LibraryId = 1,
            DiscSetId = 1,
            PlatformId = 1,
            TitleId = 1,
            Revision = Guid.NewGuid(),
            DefinitionJson = JsonSerializer.Serialize(new PublishedDiscSetDefinition("Multi-disc fixture", 2,
                [new(1, "sha1:cd-fixture", 1, 1), new(2, "sha1:cd-second", 2, 1)]))
        });
        await db.SaveChangesAsync();
        var reader = new ConsumerDiscSetRepository(db, MediaTestServices.CreateContentCatalog(db),
            RomMediaPolicyComposition.CreateDeliveryPolicy(), new DerivedDescriptorReader(db), resident.Admission,
            new ConsumerOriginalDiscLayoutReader(db), database.CreateOptions());

        var status = (await reader.ReadStatusAsync(new(actor), 1, default)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>().Value;
        status.Discs.ShouldAllBe(x => x.Readiness == "ready" && x.CdArchive == null);
        var download = (await reader.ReadForDownloadAsync(new(actor), 1, default)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found>().Value;
        download.Discs.Select(x => x.Ordinal).ShouldBe([1, 2]);
        download.Discs.ShouldAllBe(disc => disc.Release.PlatformShortName == (saturn ? "saturn" : "psx"));
        download.Discs[0].CdArchive.ShouldNotBeNull().LayoutId.ShouldBe(1);
        download.Discs[1].CdArchive.ShouldBeNull();
        download.Discs.ShouldAllBe(x => x.Readiness == "ready");
        await VerifyMixedArchiveAsync(reader, download);

        await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 2).ExecuteUpdateAsync(x => x.SetProperty(e => e.IsExposed, false));
        (await reader.ReadForDownloadAsync(new(actor), 1, default)).ShouldBeOfType<ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ItemNotFound>();
    }

    private async Task VerifyMixedArchiveAsync(ConsumerDiscSetRepository repository, ConsumerDiscSetPlan plan)
    {
        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(actor);
        var server = Substitute.For<IServerInstanceIdentity>();
        server.InstanceId.Returns(Guid.NewGuid());
        var roms = Substitute.For<IRomContentReader>();
        roms.OpenAsync(Arg.Any<RomDeliveryTarget>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>()).Returns(call =>
            Task.FromResult<Stream?>(new MemoryStream(call.Arg<RomDeliveryTarget>().RomId == 4 ? secondTrack : originals["audio.bin"])));
        var descriptors = Substitute.For<IConsumerContentArtifactResolver>();
        descriptors.ResolveAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<Stream?>(
            new MemoryStream(call.Arg<Sha256>() == Identity(secondCue).Sha256 ? secondCue : originals["disc.cue"])));
        using var tickets = new ConsumerDiscSetDownloadTickets(TimeProvider.System);
        var manifests = new IssueConsumerDiscSetManifestCommandHandler(user, repository, Substitute.For<IConsumerContentGrantIssuer>(), server,
            roms, Substitute.For<IConsumerOriginalLayoutGrantIssuer>(), descriptors, Substitute.For<IConsumerDerivedDescriptorGrantIssuer>(), Substitute.For<IConsumerDerivedDescriptorSource>());
        var status = DiscSetContracts.Status(plan, server.InstanceId);
        var issued = await new IssueConsumerDiscSetDownloadCommandHandler(user, repository, manifests, tickets).HandleAsync(
            new(plan.Id, new(1, plan.Revision, status.ObservationId, plan.Discs.Select(x => IdCoder.Encode(x.Release.ReleaseId)).ToArray(), false), "chd"));
        issued.IsError.ShouldBeFalse(issued.IsError ? string.Join(';', issued.Errors) : "");
        var archive = tickets.Resolve(issued.Value.DownloadUrl.Split('/')[^1]).ShouldNotBeNull();
        archive.Playlist.ShouldBe("disc-01/disc.chd\ndisc-02/b.cue\n");
        byte[] encoded = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var cd = Substitute.For<IRomCdArchiveDownloadReader>();
        cd.OpenAsync(Arg.Any<RomCdArchiveDownload>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(encoded)));
        using var output = new MemoryStream();
        await new ConsumerDiscSetArchiveWriter(roms, descriptors, cd).WriteAsync(output, archive);
        output.Position = 0;
        using var zip = new ZipArchive(output);
        zip.Entries.Count.ShouldBe(5);
        foreach (var (path, expected) in new Dictionary<string, byte[]>
        {
            ["disc-01/disc.chd"] = encoded,
            ["disc-02/b.cue"] = secondCue,
            ["disc-02/b-track.bin"] = secondTrack,
            ["disc-02/audio.bin"] = originals["audio.bin"],
            ["Multi-disc fixture.m3u"] = Encoding.UTF8.GetBytes(archive.Playlist)
        })
        {
            await using var entry = zip.GetEntry(path).ShouldNotBeNull().Open();
            using var bytes = new MemoryStream();
            await entry.CopyToAsync(bytes);
            bytes.ToArray().ShouldBe(expected);
        }
    }
}
