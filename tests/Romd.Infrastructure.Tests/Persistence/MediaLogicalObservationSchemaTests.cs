using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Synthetic raw observations exercise persistence integrity, without media or DVD qualification.</summary>
public sealed class MediaLogicalObservationSchemaTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly RomLogicalIdentity Identity = new(131072, MediaFactGraphFixture.Hash('a'), MediaFactGraphFixture.Short('c'), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(0));
    private static RomPs2BootLayoutFacts Boot => new(131072, 64, 16, 24, 200, 25, 123, 28, 896, "cdrom0:\\slus_203.12;1", "cdrom0:\\SLUS_203.12;1",
        "SLUS_203.12", 0x00100000, 2, RomPs2BootLayoutFacts.ProfileVersion, "fixture-boot-inspector");
    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Boot_OriginalOrNativeChd_RetainsExactInstantAndRawFactsIdempotently(bool chd, bool beforeEpoch)
    {
        await using var db = _database.CreateContext();
        var time = DateTimeOffset.UnixEpoch.AddTicks(beforeEpoch ? -4321 : 4321);
        var seed = await SeedAsync(db, time);
        var source = chd ? seed.Chd : seed.Original;
        var observation = Observe(time);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var store = Store(db);
            (await store.StageAsync(1, Retained(source), observation)).IsError.ShouldBeFalse();
            (await store.StageAsync(1, Retained(source), Observe(time.AddTicks(19)))).IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var current = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1)).ShouldNotBeNull();
        var retained = current.Inspections.ShouldHaveSingleItem();
        retained.Id.ShouldBe(observation.Id);
        retained.ObservedAt.ShouldBe(time);
        retained.Layout.ShouldBe(Boot);
        retained.Identity.ShouldBe(Identity);
        retained.SameObservation(observation).ShouldBeTrue();
        var request = await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().SingleAsync();
        request.Id.ShouldBe(observation.Id);
        request.RequestedVersion.ShouldBe(1);
        var graphs = await new MediaFactReader(db).ReadAsync([seed.Original.Id, seed.Chd.Id]);
        foreach (var graph in graphs.Values)
            graph.Verifications.ShouldHaveSingleItem().ObservedAt.ShouldBe(time);
        graphs[seed.Chd.Id].Creations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Boot_SourceCollection_RetainsObservationUntilLogicalOwnerDeletion()
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, DateTimeOffset.UnixEpoch);
        var observation = Observe(DateTimeOffset.UnixEpoch);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await Store(db).StageAsync(1, Retained(seed.Original), observation)).IsError.ShouldBeFalse();
            var roms = MediaTestServices.CreateRomRepository(db);
            await roms.SetPreferredRepresentationStagedAsync(1, seed.Chd.Outputs.Single().Id);
            await roms.ReleaseRepresentationsStagedAsync(1, [seed.Original.Outputs.Single().Id]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        (await new MediaRepresentationCleanup(db).CollectAsync(10)).ShouldBe(1);
        (await db.Verifications.AnyAsync(row => row.Id == seed.Original.Verifications.Single().Id)).ShouldBeFalse();
        var retained = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1)).ShouldNotBeNull().Inspections.ShouldHaveSingleItem();
        retained.SameObservation(observation).ShouldBeTrue();
        await MediaTestServices.CreateRomRepository(db).DeleteAsync(1);
        (await CountAsync(db, "LogicalInspections")).ShouldBe(0);
        (await CountAsync(db, "Ps2BootObservations")).ShouldBe(0);
        // At-least-once work arguments survive for the worker to acknowledge; they do not pin media facts.
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().SingleAsync()).Id.ShouldBe(observation.Id);
        (await new MediaRepresentationCleanup(db).CollectAsync(10)).ShouldBe(1);
        (await db.Files.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("output")]
    [InlineData("prefix")]
    [InlineData("missing-boot")]
    [InlineData("logical-length")]
    [InlineData("volume")]
    [InlineData("root-overlap")]
    [InlineData("executable-outside")]
    [InlineData("serial")]
    [InlineData("entry-alignment")]
    [InlineData("profile")]
    [InlineData("blank-inspector")]
    [InlineData("negative-time")]
    [InlineData("past-time")]
    [InlineData("nintendo-and-boot")]
    [InlineData("missing-flag")]
    public async Task Boot_DirectSqlContradiction_RefusesWithoutRetainingPartialFacts(string change)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, DateTimeOffset.UnixEpoch);
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var id = Guid.NewGuid();
            await InsertRawAsync(db, seed, id, change);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await CountAsync(db, "LogicalInspections")).ShouldBe(0);
        (await CountAsync(db, "Ps2BootObservations")).ShouldBe(0);
        (await db.Verifications.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData("inspection-update")]
    [InlineData("inspection-delete")]
    [InlineData("boot-update")]
    [InlineData("boot-delete")]
    [InlineData("logical-identity")]
    public async Task Boot_RetainedObservationMutation_RefusesAndPreservesAllFacts(string change)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, DateTimeOffset.UnixEpoch);
        var observation = Observe(DateTimeOffset.UnixEpoch);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await Store(db).StageAsync(1, Retained(seed.Original), observation)).IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(change switch
        {
            "inspection-update" => "UPDATE romd.\"LogicalInspections\" SET \"InspectorBuild\" = 'changed'",
            "inspection-delete" => "DELETE FROM romd.\"LogicalInspections\"",
            "boot-update" => "UPDATE romd.\"Ps2BootObservations\" SET \"InspectorBuild\" = 'changed'",
            "boot-delete" => "DELETE FROM romd.\"Ps2BootObservations\"",
            "logical-identity" => "UPDATE romd.\"Roms\" SET \"ContentSize\" = 133120",
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        }));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var retained = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1)).ShouldNotBeNull().Inspections.ShouldHaveSingleItem();
        retained.Id.ShouldBe(observation.Id);
        retained.SameObservation(observation).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 1, 1)]
    public async Task Nintendo_RawPartitionCounts_RoundTripAndRejectConflictingReplay(int partitions, int standard, int korean)
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, DateTimeOffset.UnixEpoch);
        var observation = new RomLogicalInspection(Guid.NewGuid(), Identity, "fixture-nintendo/v1", "fixture-inspector", DateTimeOffset.UnixEpoch,
            new byte[32], new RomNintendoLayoutFacts(partitions, standard, korean));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var store = Store(db);
            (await store.StageAsync(1, Retained(seed.Original), observation)).IsError.ShouldBeFalse();
            int different = partitions == 0 ? 2 : 0;
            var conflicting = new RomLogicalInspection(Guid.NewGuid(), Identity, observation.Profile, observation.InspectorBuild, observation.ObservedAt,
                new byte[32], new RomNintendoLayoutFacts(different, different, 0));
            (await store.StageAsync(1, Retained(seed.Chd), conflicting)).IsError.ShouldBeTrue();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var retained = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(1)).ShouldNotBeNull().Inspections.ShouldHaveSingleItem();
        retained.Layout.ShouldBe(observation.Layout);
        (await CountAsync(db, "Ps2BootObservations")).ShouldBe(0);
        var request = await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().SingleAsync();
        request.Id.ShouldBe(observation.Id);
        request.Cause.ShouldBe("Facts");
        request.ConversionRequested.ShouldBeFalse();
        var demand = await db.Set<Romd.Persistence.Entities.MediaReconciliationSubjectEntity>().SingleAsync();
        demand.SubjectKind.ShouldBe("LogicalRom");
        demand.SubjectId.ShouldBe(1);
        demand.RequestedVersion.ShouldBe(1);
    }

    [Fact]
    public async Task Store_WithoutCallerTransaction_RefusesBeforeWriting()
    {
        await using var db = _database.CreateContext();
        var seed = await SeedAsync(db, DateTimeOffset.UnixEpoch);
        await Should.ThrowAsync<InvalidOperationException>(() => Store(db).StageAsync(1, Retained(seed.Original), Observe(DateTimeOffset.UnixEpoch)));
        (await CountAsync(db, "LogicalInspections")).ShouldBe(0);
    }

    private static RomMediaObservationStore Store(RomdDbContext db) => new(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System));
    private static RomLogicalInspection Observe(DateTimeOffset time) => new(Guid.NewGuid(), Identity, "fixture-structure/v1", "fixture-inspector", time, new byte[32], layout: Boot);
    private static RomRetainedMeasurement Retained(RomRepresentationFacts source) => new(source.Id, source.Outputs.Single().Id, source.FileId, source.Verifications.Single().Id);
    private static Task<int> CountAsync(RomdDbContext db, string table) => db.Database.SqlQueryRaw<int>(table switch
    {
        "LogicalInspections" => "SELECT count(*)::integer AS \"Value\" FROM romd.\"LogicalInspections\"",
        "Ps2BootObservations" => "SELECT count(*)::integer AS \"Value\" FROM romd.\"Ps2BootObservations\"",
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    }).SingleAsync();
    private static async Task<(RomRepresentationFacts Original, RomRepresentationFacts Chd)> SeedAsync(RomdDbContext db, DateTimeOffset time)
    {
        db.Files.AddRange(new FileEntityPersistence { Id = 1, Sha256 = Identity.Sha256, Size = Identity.Size, SizeOnDisk = Identity.Size },
            new FileEntityPersistence { Id = 2, Sha256 = MediaFactGraphFixture.Hash('b'), Size = 4096, SizeOnDisk = 4096 });
        db.StageOriginalRom(new() { Id = 1, FileId = 1, OriginalFilename = "synthetic.iso", Sha1 = Identity.Sha1, Md5 = Identity.Md5, Crc32 = Identity.Crc32, CreatedAt = time });
        await db.PublishOriginalRomsAsync();
        int originalId = await db.RomRepresentations.Select(row => row.Id).SingleAsync();
        var original = (await new MediaFactReader(db).ReadAsync([originalId]))[originalId];
        var container = new RomContainerFacts.Chd(5, Identity.Size, 16384, 2048, 8, [0, 0, 0, 0], false, Identity.Sha1, MediaFactGraphFixture.Short('f'), []);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), MediaFactGraphFixture.Hash('b'), 4096,
            new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('e'), 2)), "fixture-chd-whole/v1", "fixture-verifier", time,
            [new(RomOutputSelector.Whole.Instance, Identity)], container, container.StoredRawSha1, container.StoredOverallSha1);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var chd = await new MediaFactPublisher(db).PublishAsync(2, container, RomReconstructionManifest.WholeFile.Instance, proof, [new(RomOutputSelector.Whole.Instance, 1, false)], []);
        await transaction.CommitAsync();
        return (original, chd);
    }
    private static async Task InsertRawAsync(RomdDbContext db, (RomRepresentationFacts Original, RomRepresentationFacts Chd) seed, Guid id, string change)
    {
        int output = (change == "output" ? seed.Chd : seed.Original).Outputs.Single().Id;
        Guid proof = change == "proof" ? Guid.NewGuid() : seed.Original.Verifications.Single().Id;
        long ticks = change == "negative-time" ? -1 : change == "past-time" ? 3155378976000000000 : DateTimeOffset.UnixEpoch.UtcTicks;
        int? partitions = change == "nintendo-and-boot" ? 0 : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."LogicalInspections" ("Id","RomId","SourceOutputId","SourceVerificationId","Profile","InspectorBuild","ObservedAtUtcTicks","Prefix",
                "NintendoPartitions","StandardKeyPartitions","KoreanKeyPartitions","HasPs2Boot")
            VALUES ({id},1,{output},{proof},'fixture-structure/v1','fixture-inspector',{ticks},{new byte[change == "prefix" ? 31 : 32]},
                CAST({partitions} AS integer),CAST({partitions} AS integer),CAST({partitions} AS integer),{change != "missing-flag"})
            """);
        if (change == "missing-boot")
            return;
        var boot = Boot;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."Ps2BootObservations" ("InspectionId","LogicalBytes","VolumeBlocks","PrimaryDescriptorBlock","RootDirectoryBlock","RootDirectoryBytes",
                "SystemCnfBlock","SystemCnfBytes","BootExecutableBlock","BootExecutableBytes","ObservedBoot2Value","BootPath","BootSerial","EntryPoint","ProgramHeaderCount","Profile","InspectorBuild")
            VALUES ({id},{(change == "logical-length" ? boot.LogicalBytes + 2048 : boot.LogicalBytes)},{(change == "volume" ? 65L : boot.VolumeBlocks)},16,24,200,
                {(change == "root-overlap" ? 24L : boot.SystemCnfBlock)},123,{(change == "executable-outside" ? 64L : boot.BootExecutableBlock)},896,
                {boot.ObservedBoot2Value},{boot.BootPath},{(change == "serial" ? "SLUS_999.99" : boot.BootSerial)},{(change == "entry-alignment" ? boot.EntryPoint + 1L : boot.EntryPoint)},2,
                {(change == "profile" ? "foreign/v1" : boot.Profile)},{(change == "blank-inspector" ? " " : boot.InspectorBuild)})
            """);
    }
}
