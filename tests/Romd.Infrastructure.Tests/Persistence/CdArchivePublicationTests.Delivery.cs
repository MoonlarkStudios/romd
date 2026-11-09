using ArchiveRead = Romd.Consumer.Application.Libraries.ConsumerLibraryReadResult<Romd.Application.Common.Content.RomCdArchiveDownload>;
using FileRead = Romd.Consumer.Application.Libraries.ConsumerLibraryReadResult<Romd.Consumer.Application.Vfs.ConsumerVfsResolvedFile>;
using ListingRead = Romd.Consumer.Application.Libraries.ConsumerLibraryReadResult<Romd.Consumer.Application.Vfs.ConsumerVfsReleasePage>;
using ManifestRead = Romd.Consumer.Application.Libraries.ConsumerLibraryReadResult<Romd.Consumer.Application.Delivery.ConsumerReleaseManifest>;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using ReleaseRead = Romd.Consumer.Application.Libraries.ConsumerLibraryReadResult<Romd.Consumer.Application.Vfs.ConsumerVfsRelease>;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Queries.PrepareReleaseDownload;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
/// Characterizes today's delivery of the published PS1 CUE/BIN disc through the real CD admission and repositories.
/// These pin current output, including cells that look surprising, so later media-policy refactors can name what they change.
/// </summary>
public sealed partial class CdArchivePublicationTests
{
    /// <summary>The real <see cref="ChdDeliveryAdmission"/> over synthetic resident native facts.</summary>
    private sealed class ResidentChd
    {
        public ResidentChd(string rid, int recipe)
        {
            var native = new ChdNativeIdentity(rid, "libmoonlark_chdr.so", 100, new string('c', 64), Decoder.UpstreamCommit.ToString(),
                "test-only-upstream", recipe, "test-only-family", new string('e', 64), true, true, true);
            var candidate = new ChdNativeCandidate("/synthetic/cd-native", native);
            Admission = new(() => Ready, () => candidate, () => Ready = false, _ => { Verified++; return native; });
        }
        public ChdDeliveryAdmission Admission { get; }
        public bool Ready { get; private set; } = true;
        public int Verified { get; private set; }
    }

    /// <summary>"admitted" matches the published decoder exactly; "arm64" changes only the RID; "recipe" changes only the recipe and has no proof.</summary>
    private static ResidentChd Resident(string scenario) => scenario switch
    {
        "arm64" => new("linux-arm64", Decoder.RecipeVersion),
        "recipe" => new(Decoder.Rid, Decoder.RecipeVersion + 1),
        _ => new(Decoder.Rid, Decoder.RecipeVersion)
    };

    private sealed record Offer(string Path, RomDeliveryFormat Format, RomDeliveryState State, string Profile, string Runtimes,
        RomDeliveryTarget? Target, ConsumerReleaseRuntime? Runtime = null)
    {
        public static Offer Of(ConsumerReleaseManifestFormat format) => new(format.RelativePath, format.Delivery.Format, format.Delivery.State,
            format.Delivery.ProfileVersion, string.Join(',', format.Delivery.RuntimeApplicability), format.Delivery.Target, format.Runtime);
    }

    /// <summary>The exact original BIN target, read from the shared CHD through its member binding.</summary>
    private RomDeliveryTarget BinOriginal(string member, int romId, RomRepresentationFacts archive, int ordinal)
    {
        var logical = Identity(originals[member]);
        var output = archive.Outputs.Single(row => row.RomId == romId && row.Selector == new RomOutputSelector.Member(ordinal));
        var content = new RomContentDescriptor(romId, output.Id, output.Identity, member, [new(archive, output.Id, true)], [], []);
        var binding = ((RomReconstruction.CdMember)content.Reconstruction).Bind(Decoder);
        return new(romId, 9, RomDeliveryFormat.Original, "catalog-" + member, logical.Sha256, logical.Size, logical.Sha256, logical.Size,
            archive.ArtifactSha256, archive.ArtifactSize, RomRepresentationEncoding.Chd, logical.Md5, logical.Sha1, binding);
    }

    [Theory]
    [InlineData("admitted")]
    [InlineData("arm64")]
    [InlineData("recipe")]
    public async Task GetManifestAsync_PublishedCueBin_OffersBinOriginalsOnlyOnMatchingHostDecoder(string scenario)
    {
        await Publish();
        await using var db = database.CreateContext();
        var resident = Resident(scenario);
        bool admitted = scenario == "admitted";
        var archives = await ConsumerArchives(db, resident.Admission);
        var facts = await ArchiveAsync(db);
        var track = Identity(originals["track.bin"]);
        var audio = Identity(originals["audio.bin"]);

        var manifest = (await new ConsumerReleaseManifestRepository(db, RomMediaPolicyComposition.CreateDeliveryPolicy(), Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db), new Romd.Persistence.MediaFacts.DerivedDescriptorReader(db),
            resident.Admission).GetManifestAsync(new(new(actor), 1))).ShouldBeOfType<ManifestRead.Found>().Value;
        var archive = await archives.FindAsync(new(actor), 1);

        manifest.PlatformShortName.ShouldBe("psx");
        // The release envelope preserves its unknown multi-file packaging and DAT-selected launch path; per-format runtime claims remain absent.
        manifest.Runtime.ShouldBe(new ConsumerReleaseRuntime("unknown", new("file", "catalog.cue"), "direct_files", track.Size + audio.Size + 17));
        (string Path, Sha256? Sha256, long Size, string Role, bool Available)[] items =
        [
            ("catalog-audio.bin",admitted ? audio.Sha256 : null,audio.Size,"rom",admitted),
            ("catalog-track.bin",admitted ? track.Sha256 : null,track.Size,"rom",admitted),
            ("catalog.cue",null,17,"rom",false)
        ];
        manifest.Items.Select(item => (item.RelativePath, item.Sha256, item.SizeBytes, item.Role, item.IsAvailable)).ShouldBe(items);
        manifest.Items.Where(item => item.RelativePath.EndsWith(".bin")).ShouldAllBe(item => item.Source is ConsumerReleaseManifestSource.Original);
        manifest.Items.Single(item => item.RelativePath == "catalog.cue").Source.ShouldBeOfType<ConsumerReleaseManifestSource.Missing>();
        if (admitted)
        {
            ((ConsumerReleaseManifestSource.Original)manifest.Items[0].Source).AvailableTarget!.RomId.ShouldBe(2);
            ((ConsumerReleaseManifestSource.Original)manifest.Items[1].Source).AvailableTarget!.RomId.ShouldBe(1);
            manifest.Items.Take(2).ShouldAllBe(item => ((ConsumerReleaseManifestSource.Original)item.Source).AvailableTarget!.FileId == 9);
        }
        // A CD member offers only its original (no CHD option), with the raw file-set profile and no per-format runtime.
        Offer Bin(string member, int romId, int ordinal) => admitted
            ? new("catalog-" + member, RomDeliveryFormat.Original, RomDeliveryState.Available, CdProfile, "",
                BinOriginal(member, romId, facts, ordinal))
            : new("catalog-" + member, RomDeliveryFormat.Original, RomDeliveryState.Unsupported, "unknown", "", null);
        manifest.Items[0].Formats.ShouldNotBeNull().Select(Offer.Of).ShouldBe([Bin("audio.bin", 2, 1)]);
        manifest.Items[1].Formats.ShouldNotBeNull().Select(Offer.Of).ShouldBe([Bin("track.bin", 1, 0)]);
        manifest.Items[2].Formats.ShouldNotBeNull().ShouldBeEmpty();
        // Only the whole-archive download carries the retained original CUE.
        if (admitted)
            ((RomReconstructionManifest.CdTracks)archive.ShouldBeOfType<ArchiveRead.Found>().Value.Representation.Manifest).Members.OfType<RomCdStoredMember>().Single().Identity
            .ShouldBe(Identity(originals["disc.cue"]));
        else
            archive.ShouldBeOfType<ArchiveRead.ItemNotFound>();
        (resident.Verified > 0).ShouldBe(admitted);
        resident.Ready.ShouldBeTrue();
    }

    [Theory]
    [InlineData("admitted")]
    [InlineData("arm64")]
    [InlineData("recipe")]
    public async Task ConsumerVfs_PublishedCueBin_ListsCatalogNamesAndResolvesBinsOnlyWithoutDatCueTemplate(string scenario)
    {
        await Publish();
        await using var db = database.CreateContext();
        var resident = Resident(scenario);
        bool admitted = scenario == "admitted";
        await ConsumerArchives(db, resident.Admission);
        var facts = await ArchiveAsync(db);
        var vfs = new ConsumerVfsRepository(db, RomMediaPolicyComposition.CreateDeliveryPolicy(), Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db), resident.Admission);
        VfsFormat[] formats = [VfsFormat.Stored, VfsFormat.Original];

        foreach (var format in formats)
        {
            // Listings evaluate current member admission for both formats while preserving every catalog path as an original.
            (await vfs.ListReleasesAsync(new(actor), 1, format: format)).ShouldBeOfType<ListingRead.Found>().Value.ReleasePaths[1]
                .ShouldBe(["catalog-track.bin", "catalog-audio.bin", "catalog.cue"], ignoreOrder: true);
            // The DAT CUE template matches no stored ROM, so the tree and every file read are unavailable before planning.
            (await vfs.GetReleaseAsync(new(actor), 1, default, format)).ShouldBeOfType<ReleaseRead.ItemNotFound>();
            (await vfs.GetFileAsync(new(actor), 1, "catalog-track.bin", default, format)).ShouldBeOfType<FileRead.ItemNotFound>();
        }
        resident.Verified.ShouldBe(admitted ? 4 : 0); // Two BIN bindings per listing, on each of the two format requests.

        // Variant: a DAT that lists only the BINs (Decide("confirm") removes the same template row) reaches the planner.
        await db.CatalogReleaseFiles.Where(row => row.Name == "catalog.cue").ExecuteDeleteAsync();
        foreach (var format in formats)
        {
            (await vfs.ListReleasesAsync(new(actor), 1, format: format)).ShouldBeOfType<ListingRead.Found>().Value.ReleasePaths[1]
                .ShouldBe(["catalog-track.bin", "catalog-audio.bin"], ignoreOrder: true);
            var release = await vfs.GetReleaseAsync(new(actor), 1, default, format);
            var file = await vfs.GetFileAsync(new(actor), 1, "catalog-track.bin", default, format);
            if (admitted)
            {
                (string Path, RomDeliveryTarget? Target)[] tree =
                [
                    ("catalog-audio.bin", BinOriginal("audio.bin", 2, facts, 1)),
                    ("catalog-track.bin", BinOriginal("track.bin", 1, facts, 0))
                ];
                release.ShouldBeOfType<ReleaseRead.Found>().Value.Tree.List("").ShouldNotBeNull()
                    .Select(entry => (entry.RelativePath, entry.File.ShouldNotBeNull().Target)).ShouldBe(tree);
                var resolved = file.ShouldBeOfType<FileRead.Found>().Value;
                resolved.File.Target.ShouldBe(BinOriginal("track.bin", 1, facts, 0));
                resolved.ReleasePaths.ShouldBe(["catalog-track.bin", "catalog-audio.bin"], ignoreOrder: true);
            }
            else
            {
                // A refused decoder surfaces as an inconsistent projection, not as an unavailable file.
                release.ShouldBeOfType<ReleaseRead.ProjectionInconsistent>();
                file.ShouldBeOfType<FileRead.ProjectionInconsistent>();
            }
        }
        (resident.Verified > 0).ShouldBe(admitted);
        resident.Ready.ShouldBeTrue();
    }

    [Theory]
    [InlineData("admitted", RomDeliveryFormat.Original)]
    [InlineData("admitted", RomDeliveryFormat.Chd)]
    [InlineData("arm64", RomDeliveryFormat.Original)]
    [InlineData("arm64", RomDeliveryFormat.Chd)]
    [InlineData("recipe", RomDeliveryFormat.Original)]
    [InlineData("recipe", RomDeliveryFormat.Chd)]
    public async Task PrepareReleaseDownload_PublishedCueBin_ZipsBinOriginalsOnlyOnMatchingHostDecoder(string scenario, RomDeliveryFormat format)
    {
        await Publish();
        await using var db = database.CreateContext();
        var resident = Resident(scenario);
        bool admitted = scenario == "admitted";
        await ConsumerArchives(db, resident.Admission);
        var facts = await ArchiveAsync(db);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actor);
        var storage = Substitute.For<IFileStorageService>();
        storage.ExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var tickets = Substitute.For<IAdminDownloadTickets>();
        AdminDownloadPlan? issued = null;
        tickets.Issue(Arg.Any<AdminDownloadPlan>()).Returns(call => { issued = call.Arg<AdminDownloadPlan>(); return new AdminDownloadTicket("/ticket", DateTimeOffset.UtcNow); });

        var result = await new PrepareReleaseDownloadQueryHandler(user, new ReleaseDownloadRepository(db), storage, tickets, Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db), RomMediaPolicyComposition.CreateDeliveryPolicy(),
            resident.Admission).HandleAsync(new(1, format));

        if (admitted)
        {
            result.IsError.ShouldBeFalse();
            var plan = issued.ShouldNotBeNull();
            // The DAT CUE template has no ROM, so the ZIP omits it; the retained original CUE is never an entry.
            plan.Filename.ShouldBe("Test-available-files.zip");
            plan.IsArchive.ShouldBeTrue();
            // A CD member offers no CHD option, so Chd falls back to each BIN original; the repository passes the canonical key.
            plan.Entries.ShouldAllBe(entry => entry.PlatformKeys!.SequenceEqual(new[] { "psx" }));
            plan.Entries.Select(entry => entry with { PlatformKeys = null }).OrderBy(entry => entry.Path, StringComparer.Ordinal).ShouldBe([
                new ExportArchiveEntry(9, "catalog-audio.bin", 2, BinOriginal("audio.bin", 2, facts, 1)),
                new ExportArchiveEntry(9, "catalog-track.bin", 1, BinOriginal("track.bin", 1, facts, 0))]);
        }
        else
        {
            result.FirstError.Code.ShouldBe("Export.FileUnavailable");
            issued.ShouldBeNull();
            // The planner refuses before any storage probe, which would report the same code.
            await storage.DidNotReceive().ExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        (resident.Verified > 0).ShouldBe(admitted);
        resident.Ready.ShouldBeTrue();
    }
}
