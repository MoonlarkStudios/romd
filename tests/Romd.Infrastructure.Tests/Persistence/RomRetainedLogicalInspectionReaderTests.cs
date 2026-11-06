using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class RomRetainedLogicalInspectionReaderTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly RomLogicalIdentity Identity = new(131072, MediaFactGraphFixture.Hash('a'), MediaFactGraphFixture.Short('c'),
        Md5.Parse(new string('d', 32)), Crc32.FromUInt32(0));
    private static RomPs2BootLayoutFacts Boot => new(131072, 64, 16, 24, 200, 25, 123, 28, 896,
        @"cdrom0:\slus_203.12;1", @"cdrom0:\SLUS_203.12;1", "SLUS_203.12", 0x00100000, 2,
        RomPs2BootLayoutFacts.ProfileVersion, "fixture-boot-inspector");
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReadAsync_CommittedOriginalOrNativeObservation_ReturnsExactRawFactsInFreshContext(bool chd, bool beforeEpoch)
    {
        await using var writer = _database.CreateContext();
        var seed = await SeedAsync(writer, chd, beforeEpoch);
        await using var reader = _database.CreateContext();
        var result = await Read(reader).ReadAsync(seed.Observation.Id);
        result.IsError.ShouldBeFalse();
        var retained = result.Value.ShouldNotBeNull();
        retained.RomId.ShouldBe(1);
        retained.Observation.Id.ShouldBe(seed.Observation.Id);
        retained.Observation.Identity.ShouldBe(Identity);
        retained.Observation.Layout.ShouldBe(Boot);
        retained.Observation.ObservedAt.ShouldBe(seed.Observation.ObservedAt);
        retained.Observation.Prefix.ToArray().ShouldBe(new byte[32]);
        retained.Observation.SameObservation(seed.Observation).ShouldBeTrue();
        reader.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_MissingOrMeasurementIdentity_DoesNotInventAnObservation()
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, false, retain: false);
        (await Read(db).ReadAsync(Guid.Empty)).FirstError.ShouldBe(RomRetainedLogicalInspectionErrors.InvalidIdentity);
        (await Read(db).ReadAsync(seed.Observation.Id)).Value.ShouldBeNull();
        (await Read(db).ReadAsync(seed.Source.Verifications.Single().Id)).Value.ShouldBeNull();
        (await Read(db).ReadAsync(Guid.NewGuid())).Value.ShouldBeNull();
        (await db.Verifications.CountAsync()).ShouldBe(2);
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_StagedTrackedOwnerChange_RefusesCallerOnlyAuthority(bool deleted)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, false);
        var owner = await db.RomFiles.AsTracking().SingleAsync();
        if (deleted)
            db.RomFiles.Remove(owner);
        else
            owner.OriginalFilename = "uncommitted.iso";
        (await Read(db).ReadAsync(seed.Observation.Id)).FirstError.ShouldBe(RomRetainedLogicalInspectionErrors.StagedChanges);
        db.ChangeTracker.Clear();
        (await Read(db).ReadAsync(seed.Observation.Id)).Value.ShouldNotBeNull().Observation.SameObservation(seed.Observation).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_FlushedUncommittedObservation_RefusesAndRollbackRestoresAbsence(bool chd)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, chd, retain: false);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await Store(db).StageAsync(1, Receipt(seed.Source), seed.Observation)).IsError.ShouldBeFalse();
            (await Read(db).ReadAsync(seed.Observation.Id)).FirstError.ShouldBe(RomRetainedLogicalInspectionErrors.StagedChanges);
            await transaction.RollbackAsync();
        }
        (await Read(db).ReadAsync(seed.Observation.Id)).Value.ShouldBeNull();
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_UncommittedOwnerDeletion_RefusesAndRollbackRestoresCommittedFacts(bool flushed)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, false);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            if (flushed)
                await db.RomFiles.Where(row => row.Id == 1).ExecuteDeleteAsync();
            else
                db.RomFiles.Remove(await db.RomFiles.AsTracking().SingleAsync());
            (await Read(db).ReadAsync(seed.Observation.Id)).FirstError.ShouldBe(RomRetainedLogicalInspectionErrors.StagedChanges);
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        (await Read(db).ReadAsync(seed.Observation.Id)).Value.ShouldNotBeNull().Observation.SameObservation(seed.Observation).ShouldBeTrue();
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task ReadAsync_AmbientTransactionWithMatchingIndependentCommit_ReturnsRetainedObservation(IsolationLevel isolation)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, false);
        await using var transaction = await db.Database.BeginTransactionAsync(isolation);
        var result = await Read(db).ReadAsync(seed.Observation.Id);
        result.IsError.ShouldBeFalse();
        result.Value.ShouldNotBeNull().Observation.SameObservation(seed.Observation).ShouldBeTrue();
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadAsync_CommittedProbeReusesCallerConnection_RefusesWithoutClaimingIndependentAuthority()
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, false);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var shared = _database.CreateOptions(options => options.UseNpgsql(db.Database.GetDbConnection()));
        (await new RomRetainedLogicalInspectionReader(db, shared).ReadAsync(seed.Observation.Id)).FirstError
            .ShouldBe(RomRetainedLogicalInspectionErrors.Unavailable);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_CollectedMeasurementSource_RetainsRawObservationForItsLogicalOwner(bool chd)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, chd);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var roms = MediaTestServices.CreateRomRepository(db);
            await roms.SetPreferredRepresentationStagedAsync(1, seed.Other.Outputs.Single().Id);
            await roms.ReleaseRepresentationsStagedAsync(1, [seed.Source.Outputs.Single().Id]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        (await new MediaRepresentationCleanup(db).CollectAsync(1)).ShouldBe(1);
        (await db.Verifications.AnyAsync(proof => proof.Id == seed.Source.Verifications.Single().Id)).ShouldBeFalse();
        (await Read(db).ReadAsync(seed.Observation.Id)).Value.ShouldNotBeNull().Observation.SameObservation(seed.Observation).ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_CachedOwnerAfterCommittedBulkDeletion_ReturnsAbsenceWithoutResurrection()
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, false);
        _ = await db.RomFiles.AsTracking().SingleAsync();
        await using (var writer = _database.CreateContext())
            await writer.RomFiles.Where(row => row.Id == 1).ExecuteDeleteAsync();
        (await Read(db).ReadAsync(seed.Observation.Id)).Value.ShouldBeNull();
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().SingleAsync()).Id.ShouldBe(seed.Observation.Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReadAsync_CommandOrCommittedProbeFailure_PropagatesExactExceptionWithoutChangingFacts(bool cancelled, bool committedProbe)
    {
        await using var writer = _database.CreateContext();
        var seed = await SeedAsync(writer, false);
        using var cancellation = new CancellationTokenSource();
        Exception held = cancelled ? new OperationCanceledException("held read cancellation", cancellation.Token)
            : new InvalidOperationException("held database failure");
        var throwing = _database.CreateOptions(options => options.AddInterceptors(new ThrowingCommand(held)));
        await using var db = new RomdDbContext(committedProbe ? _database.CreateOptions() : throwing);
        await using var transaction = committedProbe ? await db.Database.BeginTransactionAsync() : null;
        Exception? observed = null;
        try
        {
            await new RomRetainedLogicalInspectionReader(db, committedProbe ? throwing : _database.CreateOptions())
            .ReadAsync(seed.Observation.Id, cancellation.Token);
        }
        catch (Exception error) { observed = error; }
        observed.ShouldBeSameAs(held);
        if (observed is OperationCanceledException cancelledError)
            cancelledError.CancellationToken.ShouldBe(cancellation.Token);
        if (transaction is not null)
            await transaction.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await Read(writer).ReadAsync(seed.Observation.Id)).Value.ShouldNotBeNull().Observation.SameObservation(seed.Observation).ShouldBeTrue();
    }

    private RomRetainedLogicalInspectionReader Read(RomdDbContext db) => new(db, _database.CreateOptions());
    private static RomMediaObservationStore Store(RomdDbContext db) => new(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System));
    private static RomRetainedMeasurement Receipt(RomRepresentationFacts facts) =>
        new(facts.Id, facts.Outputs.Single().Id, facts.FileId, facts.Verifications.Single().Id);
    private sealed record Seed(RomRepresentationFacts Source, RomRepresentationFacts Other, RomLogicalInspection Observation);
    private async Task<Seed> SeedAsync(RomdDbContext db, bool chd, bool beforeEpoch = false, bool retain = true)
    {
        var time = DateTimeOffset.UnixEpoch.AddTicks(beforeEpoch ? -4321 : 4321).ToOffset(TimeSpan.FromHours(-5));
        db.Files.AddRange(new() { Id = 1, Sha256 = Identity.Sha256, Size = Identity.Size, SizeOnDisk = Identity.Size },
            new() { Id = 2, Sha256 = MediaFactGraphFixture.Hash('b'), Size = 4096, SizeOnDisk = 4096 });
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var publisher = new MediaFactPublisher(db);
        int romId = await publisher.CreateLogicalAsync("synthetic.iso", Identity, Guid.Empty, time);
        var identityProof = new RomRepresentationVerification(Guid.NewGuid(), Identity.Sha256, Identity.Size,
            new RomDecoderIdentity.Managed("fixture-hasher"), "complete-original/v1", "fixture-verifier", time,
            [new(RomOutputSelector.Whole.Instance, Identity)], RomContainerFacts.Identity.Instance);
        var original = await publisher.PublishAsync(1, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance,
            identityProof, [new(RomOutputSelector.Whole.Instance, romId, true)], []);
        var container = new RomContainerFacts.Chd(5, Identity.Size, 16384, 2048, 8, [0, 0, 0, 0], false,
            Identity.Sha1, MediaFactGraphFixture.Short('f'), []);
        var nativeProof = new RomRepresentationVerification(Guid.NewGuid(), MediaFactGraphFixture.Hash('b'), 4096,
            new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('e'), 2)), "complete-native/v1", "fixture-verifier", time,
            [new(RomOutputSelector.Whole.Instance, Identity)], container, container.StoredRawSha1, container.StoredOverallSha1);
        var native = await publisher.PublishAsync(2, container, RomReconstructionManifest.WholeFile.Instance, nativeProof,
            [new(RomOutputSelector.Whole.Instance, romId, false)], []);
        var observation = new RomLogicalInspection(Guid.NewGuid(), Identity, "raw-structure/v1", "fixture-inspector", time, new byte[32], layout: Boot);
        var seed = new Seed(chd ? native : original, chd ? original : native, observation);
        if (retain)
            (await Store(db).StageAsync(romId, Receipt(seed.Source), observation)).IsError.ShouldBeFalse();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        return seed;
    }
    private sealed class ThrowingCommand(Exception error) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) => throw error;
    }
}
