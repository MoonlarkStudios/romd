using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

using ContractOriginalDiscLayout = Romd.Contracts.Management.Models.OriginalDiscLayout;

using JobItem = Romd.Domain.Jobs.JobItem;
using UploadJob = Romd.Domain.Jobs.UploadJob;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class OriginalDiscLayoutTests : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid job = Guid.NewGuid();
    private readonly Guid actor = Guid.NewGuid();
    public OriginalDiscLayoutTests()
    {
        using var db = database.CreateContext();
        db.Platforms.Add(new() { Id = 1, Name = "PlayStation", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "PlayStation", BaseCompactLabel = "PS", ShortName = "psx", CanonicalKey = "psx" });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Game", NormalizedName = "game", EnrichmentStatus = "Completed" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:tracks", Name = "Game (USA)" });
        byte[] track = [1, 2, 3, 4];
        byte[] cue = Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n");
        db.Files.AddRange(File(1, track), File(2, cue), File(3, Encoding.UTF8.GetBytes("REM repair\n" + Encoding.UTF8.GetString(cue))));
        db.StageOriginalRom(new()
        {
            Id = 1,
            FileId = 1,
            OriginalFilename = "track.bin",
            Sha1 = Sha1.FromBytes(SHA1.HashData(track)),
            Md5 = Md5.FromBytes(MD5.HashData(track)),
            Crc32 = Crc32.FromUInt32(1)
        });
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = "sha1:track", Name = "track.bin", Size = track.Length, Sha1 = Sha1.FromBytes(SHA1.HashData(track)) });
        db.PublishOriginalRomsAsync().GetAwaiter().GetResult();
        db.SaveChanges();
    }
    private static FileEntityPersistence File(int id, byte[] bytes) => new() { Id = id, Sha256 = Sha256.FromBytes(SHA256.HashData(bytes)), Size = bytes.Length, SizeOnDisk = bytes.Length };
    private async Task<ContractOriginalDiscLayout> Intake(string path = "game.cue", int file = 2)
    {
        await using var db = database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));
        await service.RecordStagedAsync(job, path, path, file, OriginalCueDescriptor.Parse(Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY"), path), default);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return (await service.ListAsync(null, null, 100, default)).Value.Items.Last();
    }
    private async Task<ContractOriginalDiscLayout> Confirm(ContractOriginalDiscLayout layout, ContractOriginalDiscLayout? replaces = null)
    {
        await using var db = database.CreateContext();
        var result = await DecideAsync(db, Decode(layout.Id), new(layout.Revision, "confirm", IdCoder.Encode(1),
            [new("track.bin", IdCoder.Encode(1))], "Reviewed", replaces?.Id, replaces?.Revision), actor, default);
        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        return result.Value;
    }
    [Fact]
    public async Task Intake_ReplayAndRestart_KeepsDescriptorIdentityAndExplicitFileRoot()
    {
        var first = await Intake();
        var replay = await Intake();
        replay.Id.ShouldBe(first.Id);
        replay.Revision.ShouldBe(first.Revision);
        await using var db = database.CreateContext();
        (await db.OriginalDiscLayouts.CountAsync()).ShouldBe(1);
        (await new FileRepository(db).IsReferencedAsync(2, default)).ShouldBeTrue();
        (await new FileRepository(db).GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.MaxValue, default)).ShouldNotContain(2);
        (await db.RomFiles.CountAsync()).ShouldBe(1);
        (await db.CatalogReleaseFiles.CountAsync()).ShouldBe(1);
    }
    [Fact]
    public async Task Confirm_ExactContent_BindsIndependentDescriptorAndPreservesAcrossCatalogNameRefresh()
    {
        var accepted = await Confirm(await Intake());
        accepted.State.ShouldBe("accepted");
        await using var db = database.CreateContext();
        await db.CatalogReleaseFiles.ExecuteUpdateAsync(x => x.SetProperty(f => f.Name, "renamed.bin"));
        await using var tx = await db.Database.BeginTransactionAsync();
        var resolved = await new OriginalDiscLayoutService(db, new FileMutationLock(db)).ResolveAsync(1, 1, "sha1:tracks", default);
        resolved.ShouldNotBeNull();
        resolved.Files.Select(x => x.Path).ShouldBe(["game.cue", "track.bin"]);
        resolved.Files[0].RomId.ShouldBeNull();
        resolved.Files[0].Role.ShouldBe("descriptor");
        resolved.Files[1].RomId.ShouldBe(1);
    }
    [Fact]
    public async Task Confirm_ChangedFingerprint_IsStaleAndCannotReconnectByName()
    {
        await Confirm(await Intake());
        await using var db = database.CreateContext();
        await db.CatalogReleases.ExecuteUpdateAsync(x => x.SetProperty(r => r.Fingerprint, "other-edition"));
        await using var tx = await db.Database.BeginTransactionAsync();
        var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));
        (await service.ResolveAsync(1, 1, "other-edition", default)).ShouldBeNull();
        (await service.HasStaleAsync(1, 1, "other-edition", default)).ShouldBeTrue();
        (await service.ListAsync(null, null, 100, default)).Value.Items.Single().State.ShouldBe("stale");
    }
    [Fact]
    public async Task Confirm_InvalidReplacementAndStaleRevision_KeepPreviousValidBinding()
    {
        var original = await Confirm(await Intake());
        var replacement = await Intake("replacement.cue", 3);
        await using (var db = database.CreateContext())
        {
            var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));
            var bad = await DecideAsync(db, Decode(replacement.Id), new(replacement.Revision, "confirm", IdCoder.Encode(1), [new("track.bin", IdCoder.Encode(1))], null), actor, default);
            bad.IsError.ShouldBeTrue();
            bad.FirstError.Code.ShouldBe("OriginalLayout.ReplacementChanged");
        }
        var current = await Confirm(replacement, original);
        current.State.ShouldBe("accepted");
        await using (var db = database.CreateContext())
        {
            var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));
            var stale = await DecideAsync(db, Decode(replacement.Id), new(replacement.Revision, "detach", null, null, null), actor, default);
            stale.IsError.ShouldBeTrue();
            stale.FirstError.Code.ShouldBe("OriginalLayout.PreconditionFailed");
            var previous = await service.GetAsync(Decode(original.Id), default);
            previous.Value.State.ShouldBe("detached");
            previous.Value.Audit.Items.First().Action.ShouldBe("replaced");
            (await new FileRepository(db).IsReferencedAsync(2, default)).ShouldBeTrue();
            (await new FileRepository(db).IsReferencedAsync(1, default)).ShouldBeTrue();
        }
    }
    [Fact]
    public async Task Reject_DurableDecisionAndAudit_SurviveReplay()
    {
        var layout = await Intake();
        await using (var db = database.CreateContext())
        {
            var result = await DecideAsync(db, Decode(layout.Id), new(layout.Revision, "reject", null, null, "wrong descriptor"), actor, default);
            result.Value.State.ShouldBe("rejected");
            result.Value.Audit.Items.First().Actor.ShouldBe(actor);
        }
        (await Intake()).State.ShouldBe("rejected");
    }
    [Fact]
    public async Task Confirm_MissingCatalogCompanion_RejectsWithoutChangingPendingDescriptor()
    {
        var layout = await Intake();
        await using var db = database.CreateContext();
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = "missing", Name = "second.bin", Size = 12, Sha1 = Sha1.FromBytes(SHA1.HashData([99])) });
        await db.SaveChangesAsync();
        var result = await DecideAsync(db, Decode(layout.Id), new(layout.Revision, "confirm", IdCoder.Encode(1), [new("track.bin", IdCoder.Encode(1))], null), actor, default);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("OriginalLayout.Incomplete");
        var candidates = await new OriginalDiscLayoutService(db, new FileMutationLock(db)).CandidatesAsync(Decode(layout.Id), null, null, 100, default);
        candidates.Value.Items.Single().Requirements.Single(x => x.Name == "second.bin").OriginalAvailable.ShouldBeFalse();
    }
    [Fact]
    public async Task Get_DeletedBoundRom_ShowsRepairIssueAndRetainsOriginalBytes()
    {
        var layout = await Confirm(await Intake());
        await using var db = database.CreateContext();
        await db.RomFiles.Where(x => x.Id == 1).ExecuteDeleteAsync();
        var service = new OriginalDiscLayoutService(db, new FileMutationLock(db));
        var result = await service.GetAsync(Decode(layout.Id), default);
        result.Value.State.ShouldBe("stale");
        result.Value.Issue.ShouldBe("BoundOriginalUnavailable");
        (await new FileRepository(db).IsReferencedAsync(1, default)).ShouldBeTrue();
        await using var tx = await db.Database.BeginTransactionAsync();
        (await service.ResolveAsync(1, 1, "sha1:tracks", default)).ShouldBeNull();
        (await new ConsumerOriginalDiscLayoutReader(db).ReadIdentityAsync(1, 1, "sha1:tracks", default))
            .ShouldBe(new OriginalDiscLayoutIdentity(Decode(layout.Id), layout.Revision));
    }

    [Fact]
    public async Task ReadIdentity_DetachedLayout_ReportsAuthoritativeAbsence()
    {
        var layout = await Confirm(await Intake());
        await using var db = database.CreateContext();
        var result = await DecideAsync(db, Decode(layout.Id), new(layout.Revision, "detach", null, null, "Retired"), actor, default);
        result.IsError.ShouldBeFalse();
        await using var tx = await db.Database.BeginTransactionAsync();
        var reader = new ConsumerOriginalDiscLayoutReader(db);
        (await reader.ReadIdentityAsync(1, 1, "sha1:tracks", default)).ShouldBe(new OriginalDiscLayoutIdentity(null, null));
        (await reader.ResolveAsync(1, 1, "sha1:tracks", default)).ShouldBeNull();
    }

    [Fact]
    public async Task ReadIdentity_ReplacementWithUnavailableOriginal_ReportsNewDecision()
    {
        var original = await Confirm(await Intake());
        var replacement = await Confirm(await Intake("replacement.cue", 3), original);
        await using var db = database.CreateContext();
        await db.RomFiles.Where(x => x.Id == 1).ExecuteDeleteAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var reader = new ConsumerOriginalDiscLayoutReader(db);
        (await reader.ReadIdentityAsync(1, 1, "sha1:tracks", default))
            .ShouldBe(new OriginalDiscLayoutIdentity(Decode(replacement.Id), replacement.Revision));
        (await reader.ResolveAsync(1, 1, "sha1:tracks", default)).ShouldBeNull();
    }

    [Fact]
    public async Task ReadIdentity_PartialBindingOrStaleCatalog_ReturnsUnknownInsteadOfAbsence()
    {
        await Confirm(await Intake());
        await using var db = database.CreateContext();
        await db.OriginalDiscLayouts.ExecuteUpdateAsync(x => x.SetProperty(r => r.Revision, Guid.NewGuid()));
        await using (var tx = await db.Database.BeginTransactionAsync())
            (await new ConsumerOriginalDiscLayoutReader(db).ReadIdentityAsync(1, 1, "sha1:tracks", default)).ShouldBeNull();
        await db.CatalogReleases.ExecuteUpdateAsync(x => x.SetProperty(r => r.Fingerprint, "changed-content"));
        await using (var tx = await db.Database.BeginTransactionAsync())
            (await new ConsumerOriginalDiscLayoutReader(db).ReadIdentityAsync(1, 1, "changed-content", default)).ShouldBeNull();
    }
    [Fact]
    public async Task Reconcile_DatMatchedCueAlsoRetainsAnOriginalLayout_AndReplayIsIdempotent()
    {
        byte[] cue = Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n");
        var upload = UploadJob.Create("matched-cues", createdAt: DateTimeOffset.UtcNow);
        await using var db = database.CreateContext();
        db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
        var identity = await db.Files.SingleAsync(file => file.Id == 2);
        db.StageOriginalRom(new()
        {
            Id = 2,
            FileId = 2,
            OriginalFilename = "matched.cue",
            ContentSha256 = identity.Sha256,
            ContentSize = identity.Size,
            Sha1 = Sha1.FromSpan(SHA1.HashData(cue)),
            Md5 = Md5.FromSpan(MD5.HashData(cue)),
            Crc32 = Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(cue))
        });
        await db.PublishOriginalRomsAsync();
        db.JobItems.AddRange(
            JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "matched.cue", cue.Length, RomIngestOutcome.Ingested, 2, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()),
            JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "track.bin", 4, RomIngestOutcome.Ingested, 1, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()));
        await db.SaveChangesAsync();
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(2, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(new MemoryStream(cue)));
        var intake = new Romd.Infrastructure.Source.OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)), storage,
            Substitute.For<Romd.Admin.Application.Ingestion.Jobs.IJobItemRepository>(), new EfUnitOfWork(db), MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), clock: TimeProvider.System);
        await intake.ReconcileAsync(upload.Id, default);
        await intake.ReconcileAsync(upload.Id, default);
        var layout = (await db.OriginalDiscLayouts.Include(row => row.Files).SingleAsync());
        layout.SourceJobId.ShouldBe(upload.Id);
        layout.DescriptorFileId.ShouldBe(2);
        layout.Files.Single().RomId.ShouldBe(1);
        (await db.RomFiles.CountAsync()).ShouldBe(2);
        (await db.JobItems.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    public async Task Reconcile_UnreadableMatchedCue_RecordsItsIssueWithoutFailingTheUpload(string damage)
    {
        byte[] cue = Encoding.UTF8.GetBytes("FILE \"track.bin\" BINARY\n TRACK 01 MODE2/2352\n");
        var upload = UploadJob.Create("damaged-cue", createdAt: DateTimeOffset.UtcNow);
        await using var db = database.CreateContext();
        db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
        var identity = await db.Files.SingleAsync(file => file.Id == 2);
        db.StageOriginalRom(new()
        {
            Id = 2,
            FileId = 2,
            OriginalFilename = "matched.cue",
            ContentSha256 = identity.Sha256,
            ContentSize = identity.Size,
            Sha1 = Sha1.FromSpan(SHA1.HashData(cue)),
            Md5 = Md5.FromSpan(MD5.HashData(cue)),
            Crc32 = Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(cue))
        });
        await db.PublishOriginalRomsAsync();
        db.JobItems.Add(JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "matched.cue", cue.Length, RomIngestOutcome.Ingested, 2, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()));
        await db.SaveChangesAsync();
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(2, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream?>(damage == "missing" ? null : new MemoryStream(cue[..^4])));
        var intake = new Romd.Infrastructure.Source.OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)), storage,
            Substitute.For<Romd.Admin.Application.Ingestion.Jobs.IJobItemRepository>(), new EfUnitOfWork(db), MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), clock: TimeProvider.System);
        await intake.ReconcileAsync(upload.Id, default);
        await intake.ReconcileAsync(upload.Id, default);
        var layout = await db.OriginalDiscLayouts.Include(row => row.Files).SingleAsync();
        layout.Issue.ShouldBe("DescriptorUnavailable");
        layout.Files.ShouldBeEmpty();
        layout.DescriptorFileId.ShouldBe(2);
    }

    [Fact]
    public async Task Reconcile_OriginalDeletedBeforeFileLock_DoesNotPinStaleCandidate()
    {
        var layout = await Intake();
        var upload = UploadJob.Create("original-candidates", createdAt: DateTimeOffset.UtcNow);
        await using (var seed = database.CreateContext())
        {
            seed.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
            await seed.OriginalDiscLayouts.Where(x => x.Id == Decode(layout.Id)).ExecuteUpdateAsync(x => x.SetProperty(r => r.SourceJobId, upload.Id));
            seed.JobItems.Add(JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "track.bin", 4, RomIngestOutcome.Ingested, 1, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()));
            await seed.SaveChangesAsync();
        }
        await using var db = database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var locks = Substitute.For<IFileMutationLock>();
        locks.AcquireAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            // Simulate the last ROM reference and unreferenced file disappearing before
            // reconciliation obtains the file-ledger lock; it must re-read under that lock.
            await using var competing = database.CreateContext();
            await competing.RomFiles.Where(x => x.Id == 1).ExecuteDeleteAsync();
            (await new Romd.Persistence.MediaFacts.MediaRepresentationCleanup(competing).CollectAsync(10)).ShouldBe(1);
            await competing.Files.Where(x => x.Id == 1).ExecuteDeleteAsync();
            await new FileMutationLock(db).AcquireAsync(call.Arg<Sha256>(), call.Arg<CancellationToken>());
        });
        await new OriginalDiscLayoutService(db, locks).ReconcileStagedAsync(upload.Id, default);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        (await db.OriginalDiscLayoutFiles.CountAsync(x => x.OriginalDiscLayoutId == Decode(layout.Id))).ShouldBe(0);
        await locks.Received(1).AcquireAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Resolve_OutsideConsistentSnapshot_RejectsUnboundRead()
    {
        await using var db = database.CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => new OriginalDiscLayoutService(db, new FileMutationLock(db)).ResolveAsync(1, 1, "sha1:tracks", default));
    }
    private sealed record VersionedDecision(Guid Revision, string Action, string? ReleaseId,
        IReadOnlyList<OriginalDiscDependencyChoice>? Dependencies, string? Reason, string? ReplacesLayoutId = null, Guid? ReplacesRevision = null);

    private static Task<ErrorOr.ErrorOr<ContractOriginalDiscLayout>> DecideAsync(RomdDbContext db, int id, VersionedDecision decision, Guid actor, CancellationToken ct) =>
        new DecideOriginalDiscLayoutCommandHandler(new OriginalDiscLayoutService(db, new FileMutationLock(db)), new EfUnitOfWork(db), MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance).HandleAsync(
            new(id, new(decision.Action, decision.ReleaseId, decision.Dependencies, decision.Reason, decision.ReplacesLayoutId, decision.ReplacesRevision),
                RevisionPrecondition.For(decision.Revision.ToString()), actor), ct);

    private static int Decode(string id) { IdCoder.TryDecode(id, out int value).ShouldBeTrue(); return value; }
    public void Dispose() => database.Dispose();
}
