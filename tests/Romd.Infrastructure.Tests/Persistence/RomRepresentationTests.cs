using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class RomRepresentationTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));
    private static readonly Sha1 Sha1 = Sha1.Parse(new string('c', 40));
    private static readonly Md5 Md5 = Md5.Parse(new string('d', 32));
    private static readonly RomLogicalIdentity Identity = new(100, Logical, Sha1, Md5, Crc32.FromUInt32(12));
    private static RomContainerFacts.Rvz Rvz => new(0x00030000, 1, 5, 9, 131072, false, 100);
    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task RepresentedRom_RetainsLogicalIdentityAndOffersOnlyExistingRepresentations()
    {
        await using var db = _database.CreateContext();
        db.Files.Add(new() { Id = 1, Sha256 = Artifact, Size = 20, SizeOnDisk = 20 });
        await db.SaveChangesAsync();
        int romId;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var publisher = new MediaFactPublisher(db);
            romId = await publisher.CreateLogicalAsync("disc.iso", Identity, Guid.Empty, DateTimeOffset.UnixEpoch);
            await PublishWholeAsync(db, romId, 1, Identity, Rvz, true);
            await transaction.CommitAsync();
        }
        var repository = MediaTestServices.CreateRomRepository(db);
        var rom = (await repository.GetByIdAsync(romId)).ShouldNotBeNull();
        rom.Size.ShouldBe(100);
        rom.ContentSha256.ShouldBe(Logical);
        (await db.RomRepresentations.SingleAsync()).Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        var catalog = MediaTestServices.CreateContentCatalog(db);
        var content = (await catalog.FindAsync(Logical)).ShouldNotBeNull();
        content.ArtifactSha256.ShouldBe(Artifact);
        content.ContentSize.ShouldBe(100);
        (await repository.GetByHashAsync(md5: Md5, size: 100)).ShouldNotBeNull();
        (await repository.GetByHashAsync(md5: Md5, size: 20)).ShouldBeNull();
        // A separate opaque interpretation of the same physical artifact has its own exact output.
        var opaque = new OriginalRomFixture
        {
            FileId = 1,
            OriginalFilename = "opaque.rvz",
            Sha1 = Sha1.Parse(new string('e', 40)),
            Md5 = Md5,
            Crc32 = Crc32.FromUInt32(13)
        };
        db.StageOriginalRom(opaque);
        await db.PublishOriginalRomsAsync();
        (await catalog.FindByRomIdAsync(rom.Id)).ShouldNotBeNull().ContentSha256.ShouldBe(Logical);
        (await catalog.FindByRomIdAsync(opaque.Id)).ShouldNotBeNull().ContentSha256.ShouldBe(Artifact);
    }

    [Fact]
    public async Task Cleanup_AlternateRepresentation_RemainsReferencedUntilLastOwnerReleaseAndCollection()
    {
        await using var db = _database.CreateContext();
        int romId = await SeedOriginalAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await PublishWholeAsync(db, romId, 2, Identity, Rvz, false);
            await transaction.CommitAsync();
        }
        var files = new FileRepository(db);
        (await files.IsReferencedAsync(2, default)).ShouldBeTrue();
        (await files.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow, default)).ShouldBeEmpty();
        var stats = await files.GetStorageStatsAsync(default);
        stats.Breakdown.Single(row => row.Category == "rom_unmatched").Size.ShouldBe(120);
        await MediaTestServices.CreateRomRepository(db).DeleteAsync(romId);
        (await db.RomRepresentations.CountAsync()).ShouldBe(2);
        (await new MediaRepresentationCleanup(db).CollectAsync(10)).ShouldBe(2);
        (await db.RomRepresentations.CountAsync()).ShouldBe(0);
        (await files.IsReferencedAsync(2, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task Cleanup_ConcurrentProofPublication_SkipsLockedUnownedGraphAndRetainsEveryBuildUntilCollection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var db = _database.CreateContext();
        int romId = await SeedOriginalAsync(db);
        RomRepresentationFacts alternate;
        RomRepresentationVerification second;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            alternate = await PublishWholeAsync(db, romId, 2, Identity, Rvz, false);
            second = new(Guid.NewGuid(), Artifact, 20, new RomDecoderIdentity.Managed("fixture-decoder-2"),
                "fixture-whole/v1", "fixture-verifier-2", DateTimeOffset.UnixEpoch.AddTicks(17),
                [new(RomOutputSelector.Whole.Instance, Identity)], Rvz);
            (await new MediaFactPublisher(db).AppendVerificationAsync(alternate.Id, second, ct)).ShouldBeTrue();
            await MediaTestServices.CreateRomRepository(db).ReleaseRepresentationsStagedAsync(romId, [alternate.Outputs.Single().Id], ct);
            await transaction.CommitAsync(ct);
        }
        var third = new RomRepresentationVerification(Guid.NewGuid(), Artifact, 20, new RomDecoderIdentity.Managed("fixture-decoder-3"),
            "fixture-whole/v1", "fixture-verifier-3", DateTimeOffset.UnixEpoch.AddTicks(31),
            [new(RomOutputSelector.Whole.Instance, Identity)], Rvz);
        await using var publishing = _database.CreateContext();
        await using var proofTransaction = await publishing.Database.BeginTransactionAsync(ct);
        (await new MediaFactPublisher(publishing).AppendVerificationAsync(alternate.Id, third, ct)).ShouldBeTrue();
        await using var collecting = _database.CreateContext();
        (await new MediaRepresentationCleanup(collecting).CollectAsync(256, ct)).ShouldBe(0);
        (await collecting.Verifications.Where(row => row.RepresentationId == alternate.Id).Select(row => row.Id).ToArrayAsync(ct))
            .ShouldBe(new[] { alternate.Verifications.Single().Id, second.Id }, ignoreOrder: true);
        (await new FileRepository(collecting).IsReferencedAsync(2, ct)).ShouldBeTrue();
        await proofTransaction.CommitAsync(ct);
        (await collecting.Verifications.Where(row => row.RepresentationId == alternate.Id).Select(row => row.Id).ToArrayAsync(ct))
            .ShouldBe(new[] { alternate.Verifications.Single().Id, second.Id, third.Id }, ignoreOrder: true);
        (await new MediaRepresentationCleanup(collecting).CollectAsync(256, ct)).ShouldBe(1);
        (await collecting.Verifications.AnyAsync(row => row.RepresentationId == alternate.Id, ct)).ShouldBeFalse();
        (await new FileRepository(collecting).IsReferencedAsync(2, ct)).ShouldBeFalse();
        (await new FileRepository(collecting).IsReferencedAsync(1, ct)).ShouldBeTrue();
        (await collecting.Files.CountAsync(ct)).ShouldBe(2);
        (await collecting.RepresentationOutputs.SingleAsync(ct)).RomId.ShouldBe(romId);
    }

    [Fact]
    public async Task PreferredOutputWithoutOwnedRepresentation_IsRejectedAtCommit()
    {
        await using var db = _database.CreateContext();
        int romId = await SeedOriginalAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.RomFiles.Where(row => row.Id == romId).ExecuteUpdateAsync(set => set.SetProperty(row => row.PreferredOutputId, int.MaxValue));
        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() => transaction.CommitAsync());
        error.SqlState.ShouldBe(Npgsql.PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Provisioner_UnknownMigration_RefusesToPublishOlderVersion()
    {
        await using var db = _database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO romd.\"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") VALUES ('29990101000000_FutureSchema','10.0.13')");
        await Should.ThrowAsync<InvalidOperationException>(() => PostgreSqlSchemaProvisioner.RefuseUnknownMigrationsAsync(db));
    }

    [Fact]
    public async Task UploadOutcomeCommit_DeferredConstraintRejection_IsPermanentAndRollsBackOutcome()
    {
        await using var db = _database.CreateContext();
        int romId = await SeedOriginalAsync(db);
        int? originalOutputId = (await db.RomFiles.SingleAsync()).PreferredOutputId;
        var job = UploadJob.Create("disc.iso", createdAt: DateTimeOffset.UtcNow);
        job.Start("fixture", startedAt: DateTimeOffset.UtcNow);
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 1);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        var jobs = new UploadJobRepository(db, TimeProvider.System);
        await using (var fixtureAcceptance = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await jobs.AddStagedAsync(job);
            await fixtureAcceptance.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(jobs, db, TimeProvider.System, new());
        var claim = (await claims.TryClaimExecutionAsync(job.Id, "fixture"))!;
        using var fence = JobExecutionFenceScope.Enter(job.Id, claim.FenceToken, TimeProvider.System);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        await db.RomFiles.Where(row => row.Id == romId).ExecuteUpdateAsync(set => set.SetProperty(row => row.PreferredOutputId, int.MaxValue));
        var outcome = JobItem.ForRom(job.Id, "disc.iso", 100, RomIngestOutcome.Ingested, romId, null, null, null, createdAt: DateTimeOffset.UtcNow);
        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() =>
            UploadOutcomeCommit.CommitAsync(work, transaction, new JobItemRepository(db), outcome, default));
        error.SqlState.ShouldBe(Npgsql.PostgresErrorCodes.ForeignKeyViolation);
        IngestionFailure.Classify(error).ShouldBe(IngestionFailureKind.Permanent);
        await using var verify = _database.CreateContext();
        (await verify.JobItems.CountAsync()).ShouldBe(0);
        (await verify.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(originalOutputId);
        (await verify.Jobs.SingleAsync()).DeferralCount.ShouldBe(0);
    }

    private static async Task<int> SeedOriginalAsync(RomdDbContext db)
    {
        db.Files.AddRange(new FileEntityPersistence { Id = 1, Sha256 = Logical, Size = 100, SizeOnDisk = 100 },
            new FileEntityPersistence { Id = 2, Sha256 = Artifact, Size = 20, SizeOnDisk = 20 });
        db.StageOriginalRom(new() { Id = 1, FileId = 1, OriginalFilename = "disc.iso", Sha1 = Sha1, Md5 = Md5, Crc32 = Identity.Crc32 });
        await db.PublishOriginalRomsAsync();
        return 1;
    }

    private static async Task<RomRepresentationFacts> PublishWholeAsync(RomdDbContext db, int romId, int fileId, RomLogicalIdentity identity, RomContainerFacts container, bool preferred)
    {
        var file = await db.Files.SingleAsync(row => row.Id == fileId);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), file.Sha256, file.Size, new RomDecoderIdentity.Managed("fixture-decoder"),
            "fixture-whole/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, [new(RomOutputSelector.Whole.Instance, identity)], container);
        return await new MediaFactPublisher(db).PublishAsync(fileId, container, RomReconstructionManifest.WholeFile.Instance, proof,
            [new(RomOutputSelector.Whole.Instance, romId, preferred)], []);
    }
}
