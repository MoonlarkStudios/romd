using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Concurrency;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Common.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Catalog;
using Romd.Persistence.Repositories;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Storage;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Endpoints.ConsumerDeliveryEndpointsTests;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class DiscSetDeliveryEndpointsTests
{
    [Fact]
    public async Task Manifest_OriginalCueBinCompanions_RedeemsEveryBoundFileAndReturnsStructuredSet()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, revision, expected) = await SeedDiscSet(fixture, seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "disc-set@test", seed.LibraryId);
        var listing = (await client.GetFromJsonAsync<Page<ConsumerDiscSetStatusDto>>($"/api/disc-sets?titleId={IdCoder.Encode(101)}"))?.Items;
        listing.ShouldNotBeNull();
        listing.Single().DiscSetId.ShouldBe(IdCoder.Encode(id));
        listing.Single().Discs.Count.ShouldBe(2);
        var response = await client.PostAsync($"/api/disc-sets/{IdCoder.Encode(id)}/manifest?revision={revision:D}", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var manifest = (await response.Content.ReadFromJsonAsync<ConsumerDiscSetManifestDto>(ConsumerJsonOptions))!;
        manifest.Revision.ShouldBe(revision);
        manifest.HasAllExpectedDiscs.ShouldBeTrue();
        manifest.Playlist.Text.ShouldBe("disc-01/game.cue\ndisc-02/game.cue\n");
        manifest.Playlist.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.Playlist.Text))));
        manifest.Discs.Select(x => x.Ordinal).ShouldBe([1, 2]);
        foreach (var file in manifest.Discs.SelectMany(x => x.Files))
        {
            using var anonymous = fixture.Factory.CreateClient();
            var redeemed = await anonymous.GetAsync(file.ContentGrant.DownloadUrl);
            redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);
            byte[] bytes = await redeemed.Content.ReadAsByteArrayAsync();
            bytes.ShouldBe(expected[file.RelativePath]);
            file.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        using (var db = fixture.CreateSetupDbContext())
            await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 102).ExecuteUpdateAsync(x => x.SetProperty(e => e.IsExposed, false));
        var revoked = await client.PostAsync($"/api/disc-sets/{IdCoder.Encode(id)}/manifest?revision={revision:D}", null);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Already-issued capabilities retain existing bounded lifetime semantics and exact bytes.
        var first = manifest.Discs[0].Files[0];
        (await client.GetByteArrayAsync(first.ContentGrant.DownloadUrl)).ShouldBe(expected[first.RelativePath]);
    }

    [Fact]
    public async Task Manifest_StaleRevisionAndAnonymous_FailWithoutDeliveringAReplacement()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, revision, _) = await SeedDiscSet(fixture, seed);
        using var anonymous = fixture.Factory.CreateClient();
        (await anonymous.PostAsync($"/api/disc-sets/{IdCoder.Encode(id)}/manifest?revision={revision:D}", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "disc-set@test", seed.LibraryId);
        // A stale pin of a visible set is a conflict, never an unknown set (docs/decisions/api-contract-policy.md,
        // "Idempotency And Concurrency").
        using var stale = await client.PostAsync($"/api/disc-sets/{IdCoder.Encode(id)}/manifest?revision={Guid.NewGuid():D}", null);
        using var staleBody = await ProblemEnvelope.ReadAsync(stale, 409, "DiscSet.StaleRevision");
        var status = (await client.GetFromJsonAsync<ConsumerDiscSetStatusDto>($"/api/disc-sets/{IdCoder.Encode(id)}/status"))!;
        using var staleSelection = await client.PostAsJsonAsync($"/api/disc-sets/{IdCoder.Encode(id)}/selection-manifest",
            new ConsumerDiscSetSelectionRequest(1, Guid.NewGuid(), status.ObservationId, [IdCoder.Encode(101), IdCoder.Encode(102)], false));
        using var staleSelectionBody = await ProblemEnvelope.ReadAsync(staleSelection, 409, "DiscSet.StaleRevision");
        (await client.PostAsync($"/api/disc-sets/{IdCoder.Encode(id + 1000)}/manifest?revision={revision:D}", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StatusSelectionAndAccess_PartialConsentExactRenewalAndAllMemberAuthorization()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, revision, _) = await SeedDiscSet(fixture, seed);
        using (var db = fixture.CreateSetupDbContext())
        {
            var published = await db.MaterializedLibraryDiscSets.SingleAsync();
            var definition = JsonSerializer.Deserialize<PublishedDiscSetDefinition>(published.DefinitionJson)!;
            await db.MaterializedLibraryDiscSets.ExecuteUpdateAsync(x => x.SetProperty(e => e.DefinitionJson,
                JsonSerializer.Serialize(definition with { ExpectedDiscCount = 3 })));
            await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 102).ExecuteUpdateAsync(x =>
                x.SetProperty(e => e.IsOwned, false).SetProperty(e => e.IsComplete, false));
        }
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "partial@test", seed.LibraryId);
        string root = $"/api/disc-sets/{IdCoder.Encode(id)}";
        var status = (await client.GetFromJsonAsync<ConsumerDiscSetStatusDto>(root + "/status"))!;
        status.ContractVersion.ShouldBe(1);
        status.MissingExpectedOrdinals.ShouldBe([3]);
        status.Discs.Select(x => x.Readiness).ShouldBe(["ready", "missing-media"]);
        var selection = new ConsumerDiscSetSelectionRequest(1, revision, status.ObservationId, [IdCoder.Encode(101)], false);
        (await client.PostAsJsonAsync(root + "/selection-manifest", selection)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var response = await client.PostAsJsonAsync(root + "/selection-manifest", selection with { AllowPartial = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var manifest = (await response.Content.ReadFromJsonAsync<ConsumerDiscSetManifestDto>(ConsumerJsonOptions))!;
        manifest.Discs.Single().Ordinal.ShouldBe(1);
        manifest.Selection!.Completeness.ShouldBe("partial");
        var renewed = await client.PostAsJsonAsync(root + "/selection-manifest", selection with { AllowPartial = true, RequiredSnapshotId = manifest.DeliverySnapshotId });
        renewed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(root + "/selection-manifest", selection with { AllowPartial = true, RequiredSnapshotId = new string('0', 64) })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var access = new ConsumerDiscSetAccessRequest(1, manifest.ServerInstanceId, revision, manifest.DeliverySnapshotId,
            manifest.Selection.SelectedReleaseIds, manifest.Discs.Select(x => new ConsumerDiscSetLayoutVersionDto(x.ReleaseId, x.LayoutId, x.LayoutRevision)).ToArray(), true);
        var allowed = await client.PostAsJsonAsync(root + "/access/preview", access);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        (await allowed.Content.ReadFromJsonAsync<ConsumerDiscSetAccessDto>())!.Allowed.ShouldBeTrue();
        using (var db = fixture.CreateSetupDbContext())
            await db.MaterializedLibraryReleases.Where(x => x.CatalogReleaseId == 102)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.IsExposed, false));
        (await client.GetAsync(root + "/status")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var denied = (await (await client.PostAsJsonAsync(root + "/access/preview", access)).Content.ReadFromJsonAsync<ConsumerDiscSetAccessDto>())!;
        denied.Allowed.ShouldBeFalse();
        denied.TitleId.ShouldBeNull();
        denied.SystemKey.ShouldBeNull();
        (await client.PostAsJsonAsync(root + "/selection-manifest", selection with { AllowPartial = true })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Manifest_RetainedDescriptors_RedeemSeparateImmutableCapabilitiesAndRejectOtherLanes()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, revision, expected) = await SeedDiscSet(fixture, seed);
        await SeedOriginalLayouts(fixture);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "attachment@test", seed.LibraryId);
        var response = await client.PostAsync($"/api/disc-sets/{IdCoder.Encode(id)}/manifest?revision={revision:D}", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var manifest = (await response.Content.ReadFromJsonAsync<ConsumerDiscSetManifestDto>(ConsumerJsonOptions))!;
        manifest.Discs.All(x => x.LayoutId != null && x.LayoutRevision != null).ShouldBeTrue();
        using var anonymous = fixture.Factory.CreateClient();
        foreach (var disc in manifest.Discs)
        {
            var descriptor = disc.Files.Single(x => x.RelativePath.EndsWith("game.cue", StringComparison.Ordinal));
            descriptor.ContentGrant.DownloadUrl.ShouldStartWith("/delivery/original-layout/");
            (await anonymous.GetByteArrayAsync(descriptor.ContentGrant.DownloadUrl)).ShouldBe(expected[descriptor.RelativePath]);
            string token = descriptor.ContentGrant.DownloadUrl[(descriptor.ContentGrant.DownloadUrl.LastIndexOf('/') + 1)..];
            (await anonymous.GetAsync("/delivery/content/" + token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await anonymous.GetAsync("/delivery/bios/" + token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        var access = new ConsumerDiscSetAccessRequest(1, manifest.ServerInstanceId, revision, manifest.DeliverySnapshotId,
            manifest.Selection!.SelectedReleaseIds, manifest.Discs.Select(x => new ConsumerDiscSetLayoutVersionDto(x.ReleaseId, x.LayoutId, x.LayoutRevision)).ToArray(), false);
        Guid repairedRevision = Guid.NewGuid();
        using (var db = fixture.CreateSetupDbContext())
            await db.OriginalDiscLayouts.ExecuteUpdateAsync(x => x.SetProperty(e => e.Revision, repairedRevision));
        var status = (await client.GetFromJsonAsync<ConsumerDiscSetStatusDto>($"/api/disc-sets/{IdCoder.Encode(id)}/status"))!;
        status.Discs.All(x => x.Readiness == "stale-layout").ShouldBeTrue();
        (await client.PostAsJsonAsync($"/api/disc-sets/{IdCoder.Encode(id)}/access/preview", access)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using (var db = fixture.CreateSetupDbContext())
            await db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(x => x.SetProperty(e => e.BindingRevision, repairedRevision));
        var changed = await client.PostAsJsonAsync($"/api/disc-sets/{IdCoder.Encode(id)}/access/preview", access);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        (await changed.Content.ReadFromJsonAsync<ConsumerDiscSetAccessDto>())!.Allowed.ShouldBeFalse();
        var first = manifest.Discs[0].Files.Single(x => x.RelativePath.EndsWith("game.cue", StringComparison.Ordinal));
        (await anonymous.GetByteArrayAsync(first.ContentGrant.DownloadUrl)).ShouldBe(expected[first.RelativePath]);
    }

    [Theory]
    [InlineData("detach")]
    [InlineData("replace")]
    [InlineData("unavailable")]
    public async Task Access_NonReadyAttachment_DistinguishesAuthoritativeDecisionFromMissingOriginal(string change)
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, revision, _) = await SeedDiscSet(fixture, seed);
        await SeedOriginalLayouts(fixture);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "attachment-access@test", seed.LibraryId);
        string root = $"/api/disc-sets/{IdCoder.Encode(id)}";
        var response = await client.PostAsync(root + $"/manifest?revision={revision:D}", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var manifest = (await response.Content.ReadFromJsonAsync<ConsumerDiscSetManifestDto>(ConsumerJsonOptions))!;
        var access = new ConsumerDiscSetAccessRequest(1, manifest.ServerInstanceId, revision, manifest.DeliverySnapshotId,
            manifest.Selection!.SelectedReleaseIds, manifest.Discs.Select(x => new ConsumerDiscSetLayoutVersionDto(x.ReleaseId, x.LayoutId, x.LayoutRevision)).ToArray(), false);
        using (var db = fixture.CreateSetupDbContext())
        {
            var original = await db.OriginalDiscLayouts.SingleAsync(x => x.ReleaseId == 101);
            var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));
            if (change == "detach")
            {
                var decision = await new DecideOriginalDiscLayoutCommandHandler(service, new EfUnitOfWork(db), MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance).HandleAsync(
                    new(original.Id, new("detach", null, null, "Retired"), RevisionPrecondition.For(original.Revision.ToString()), Guid.NewGuid()));
                decision.IsError.ShouldBeFalse();
            }
            else if (change == "replace")
            {
                var replacement = new OriginalDiscLayoutEntity
                {
                    SourceJobId = Guid.NewGuid(),
                    SourceRelativePath = "replacement.cue",
                    EntryPoint = "game.cue",
                    DescriptorFileId = 1000,
                    DependencyPathsJson = "[\"track.bin\"]"
                };
                db.OriginalDiscLayouts.Add(replacement);
                await db.SaveChangesAsync();
                var decision = await new DecideOriginalDiscLayoutCommandHandler(service, new EfUnitOfWork(db), MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance).HandleAsync(
                    new(replacement.Id, new("confirm", IdCoder.Encode(101),
                    [new("track.bin", IdCoder.Encode(1001)), new("game.sbi", IdCoder.Encode(1002))], "Reviewed replacement",
                    IdCoder.Encode(original.Id), original.Revision), RevisionPrecondition.For(replacement.Revision.ToString()), Guid.NewGuid()));
                decision.IsError.ShouldBeFalse();
            }
            if (change != "detach")
                await db.RomFiles.Where(x => x.Id == 1001).ExecuteDeleteAsync();
        }
        var status = (await client.GetFromJsonAsync<ConsumerDiscSetStatusDto>(root + "/status"))!;
        status.Discs[0].Readiness.ShouldNotBe("ready");
        var checkedAccess = await client.PostAsJsonAsync(root + "/access/preview", access);
        if (change == "unavailable")
            checkedAccess.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        else
        {
            checkedAccess.StatusCode.ShouldBe(HttpStatusCode.OK, await checkedAccess.Content.ReadAsStringAsync());
            var denied = (await checkedAccess.Content.ReadFromJsonAsync<ConsumerDiscSetAccessDto>())!;
            denied.Allowed.ShouldBeFalse();
            denied.LayoutVersions.ShouldBe(access.LayoutVersions);
            denied.TitleId.ShouldBeNull();
            denied.SystemKey.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Access_PublishedCatalogInconsistencyReturnsConflictRatherThanRevocation()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, revision, _) = await SeedDiscSet(fixture, seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "inconsistent@test", seed.LibraryId);
        var manifest = (await (await client.PostAsync($"/api/disc-sets/{IdCoder.Encode(id)}/manifest?revision={revision:D}", null))
            .Content.ReadFromJsonAsync<ConsumerDiscSetManifestDto>(ConsumerJsonOptions))!;
        var request = new ConsumerDiscSetAccessRequest(1, manifest.ServerInstanceId, revision, manifest.DeliverySnapshotId,
            manifest.Selection!.SelectedReleaseIds, manifest.Discs.Select(x => new ConsumerDiscSetLayoutVersionDto(x.ReleaseId, x.LayoutId, x.LayoutRevision)).ToArray(), false);
        using (var db = fixture.CreateSetupDbContext())
            await db.CatalogReleases.Where(x => x.Id == 102)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.Fingerprint, "replacement"));
        (await client.GetAsync($"/api/disc-sets/{IdCoder.Encode(id)}/status")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync($"/api/disc-sets/{IdCoder.Encode(id)}/access/preview", request)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Access_CoherentPublishedMembershipRevisionChangeRevokesOldIdentity()
    {
        await using var fixture = await ConsumerDeliveryFixture.CreateAsync();
        var seed = await fixture.SeedDeliveryDataAsync();
        var (id, revision, _) = await SeedDiscSet(fixture, seed);
        using var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), "published@test", seed.LibraryId);
        var manifest = (await (await client.PostAsync($"/api/disc-sets/{IdCoder.Encode(id)}/manifest?revision={revision:D}", null))
            .Content.ReadFromJsonAsync<ConsumerDiscSetManifestDto>(ConsumerJsonOptions))!;
        var access = new ConsumerDiscSetAccessRequest(1, manifest.ServerInstanceId, revision, manifest.DeliverySnapshotId,
            manifest.Selection!.SelectedReleaseIds, manifest.Discs.Select(x => new ConsumerDiscSetLayoutVersionDto(x.ReleaseId, x.LayoutId, x.LayoutRevision)).ToArray(), false);
        using (var db = fixture.CreateSetupDbContext())
            await db.MaterializedLibraryDiscSets.ExecuteUpdateAsync(x => x.SetProperty(e => e.Revision, Guid.NewGuid()));
        var response = await client.PostAsJsonAsync($"/api/disc-sets/{IdCoder.Encode(id)}/access/preview", access);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var denied = (await response.Content.ReadFromJsonAsync<ConsumerDiscSetAccessDto>())!;
        denied.Allowed.ShouldBeFalse();
        denied.Revision.ShouldBe(revision);
        denied.DeliverySnapshotId.ShouldBe(manifest.DeliverySnapshotId);
    }

    private static async Task SeedOriginalLayouts(ConsumerDeliveryFixture fixture)
    {
        using var db = fixture.CreateSetupDbContext();
        await db.CatalogReleaseFiles.Where(x => (x.CatalogReleaseId == 101 || x.CatalogReleaseId == 102) && x.Name == "game.cue").ExecuteDeleteAsync();
        for (int ordinal = 1; ordinal <= 2; ordinal++)
        {
            var layoutRevision = Guid.NewGuid();
            db.OriginalDiscLayouts.Add(new()
            {
                State = "accepted",
                SourceJobId = Guid.NewGuid(),
                SourceRelativePath = "original/game.cue",
                EntryPoint = "game.cue",
                DescriptorFileId = ordinal * 1000,
                PlatformId = 1,
                ReleaseId = 100 + ordinal,
                Fingerprint = "disc-set:" + ordinal,
                Revision = layoutRevision,
                DependencyPathsJson = "[\"track.bin\"]",
                Files = [new() { Path = "track.bin", RomId = ordinal * 1000 + 1, FileId = ordinal * 1000 + 1, BindingRevision = layoutRevision },
                    new() { Path = "game.sbi", RomId = ordinal * 1000 + 2, FileId = ordinal * 1000 + 2, BindingRevision = layoutRevision }]
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<(int Id, Guid Revision, Dictionary<string, byte[]> Expected)> SeedDiscSet(ConsumerDeliveryFixture fixture, DeliverySeed seed)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IContentAddressableStore>();
        using var db = fixture.CreateSetupDbContext();
        db.Titles.Add(new TitleEntity { Id = 101, PlatformId = 1, Name = "Disc Game", NormalizedName = "disc game", EnrichmentStatus = "Completed" });
        var members = new List<DiscSetMember>();
        var expected = new Dictionary<string, byte[]>();
        for (int ordinal = 1; ordinal <= 2; ordinal++)
        {
            int releaseId = 100 + ordinal;
            string name = $"Disc Game (USA) (Disc {ordinal})";
            db.SourceEntries.Add(new() { Id = releaseId, CatalogSourceId = 1, PlatformId = 1, EntryKey = name, Name = name });
            db.CatalogReleases.Add(new() { Id = releaseId, PlatformId = 1, CatalogTitleId = 101, Name = name, Fingerprint = "disc-set:" + ordinal });
            db.CatalogReleaseSources.Add(new() { CatalogReleaseId = releaseId, SourceEntryId = releaseId, ProviderClaimKey = "disc:" + ordinal, AssertedTitleId = 101 });
            db.MaterializedLibraryReleases.Add(new()
            {
                CatalogReleaseId = releaseId,
                LibraryId = seed.LibraryId,
                TitleId = 101,
                PlatformId = 1,
                DatGameId = releaseId,
                DatFileId = 1,
                IsOwned = true,
                IsExposed = true,
                IsComplete = true,
                IsEligible = true
            });
            members.Add(new(name, name, "disc-set:" + ordinal, ordinal, 101));
            int index = 0;
            foreach (string path in new[] { "game.cue", "track.bin", "game.sbi" })
            {
                int fileId = ordinal * 1000 + index++;
                byte[] bytes = Encoding.UTF8.GetBytes(path.EndsWith(".cue") ? $"REM disc {ordinal}\nFILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n" : path + "-disc-" + ordinal);
                await using var stream = new MemoryStream(bytes);
                var stored = await store.StoreAsync(stream);
                var sha1 = Sha1.FromBytes(SHA1.HashData(bytes));
                db.Files.Add(new() { Id = fileId, Sha256 = stored.Key.Hash, Size = bytes.Length, SizeOnDisk = stored.StoredSize });
                db.StageOriginalRom(new() { Id = fileId, FileId = fileId, OriginalFilename = path, Sha1 = sha1, Md5 = Md5.FromBytes(MD5.HashData(bytes)), Crc32 = Crc32.FromUInt32((uint)fileId) });
                db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = releaseId, FileFingerprint = "disc-file:" + fileId, Name = path, Size = bytes.Length, Sha1 = sha1 });
                expected.Add($"disc-{ordinal:D2}/" + path, bytes);
            }
        }
        var definition = new DiscSetDefinition("Disc Game (USA)", 2, members, [], "manual-v1");
        var row = new DiscSetEntity
        {
            PlatformId = 1,
            CatalogSourceId = 1,
            TitleIds = [101],
            ProposalKey = new string('a', 64),
            ProposalJson = JsonSerializer.Serialize(definition),
            CorrectionJson = JsonSerializer.Serialize(definition),
            Decision = "accepted"
        };
        db.DiscSets.Add(row);
        await db.PublishOriginalRomsAsync();
        await db.SaveChangesAsync();
        db.MaterializedLibraryDiscSets.Add(new()
        {
            LibraryId = seed.LibraryId,
            DiscSetId = row.Id,
            PlatformId = 1,
            TitleId = 101,
            Revision = row.Revision,
            DefinitionJson = JsonSerializer.Serialize(new PublishedDiscSetDefinition(definition.Name,
                definition.ExpectedDiscCount, members.Select(m => new PublishedDiscSetMember(100 + m.Ordinal, m.Fingerprint, m.Ordinal, 101)).ToArray()))
        });
        await db.SaveChangesAsync();
        return (row.Id, row.Revision, expected);
    }
}
