using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class MediaCdObservationSchemaTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Boot_VerifyOnlyObservation_IsIdempotentWithoutEncoderProvenance()
    {
        await using var db = _database.CreateContext();
        var seed = await MediaFactGraphFixture.CreateAsync(db);
        Guid first;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var writer = new MediaCreationFactWriter(db);
            first = await writer.StageCdBootAsync(seed.Cd.Id, seed.Cd.Verifications.Single().Id, "fixture-inspector", DateTimeOffset.UnixEpoch, MediaFactGraphFixture.Boot, default);
            (await writer.StageCdBootAsync(seed.Cd.Id, seed.Cd.Verifications.Single().Id, "fixture-inspector", DateTimeOffset.UnixEpoch.AddTicks(9), MediaFactGraphFixture.Boot, default)).ShouldBe(first);
            await transaction.CommitAsync();
        }
        var retained = (await new MediaFactReader(db).ReadAsync([seed.Cd.Id]))[seed.Cd.Id];
        retained.CdBootInspections.Single().Id.ShouldBe(first);
        retained.Creations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Creation_ReplayedIdentity_IsIdempotentAndRejectsChangedRequest()
    {
        await using var db = _database.CreateContext();
        var seed = await MediaFactGraphFixture.CreateAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var writer = new MediaCreationFactWriter(db);
            Guid boot = await writer.StageCdBootAsync(seed.Cd.Id, seed.Cd.Verifications.Single().Id, "fixture-inspector", DateTimeOffset.UnixEpoch, MediaFactGraphFixture.Boot, default);
            var creation = seed.Creation(boot);
            await writer.StageCreationAsync(seed.Cd.Id, creation, default);
            await writer.StageCreationAsync(seed.Cd.Id, creation, default);
            await Should.ThrowAsync<InvalidDataException>(() => writer.StageCreationAsync(seed.Cd.Id, seed.Creation(boot, creation.Id, requestBytes: 101), default));
            await transaction.CommitAsync();
        }
        (await new MediaFactReader(db).ReadAsync([seed.Cd.Id]))[seed.Cd.Id].Creations.Count.ShouldBe(1);
        (await db.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(seed.Source.Outputs.Single().Id);
    }

    [Fact]
    public async Task Creation_SourceRelease_RetainsHistoryUntilTargetCollection()
    {
        await using var db = _database.CreateContext();
        var seed = await MediaFactGraphFixture.CreateAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var writer = new MediaCreationFactWriter(db);
            Guid boot = await writer.StageCdBootAsync(seed.Cd.Id, seed.Cd.Verifications.Single().Id, "fixture-inspector", DateTimeOffset.UnixEpoch, MediaFactGraphFixture.Boot, default);
            await writer.StageCreationAsync(seed.Cd.Id, seed.Creation(boot), default);
            await db.RomFiles.Where(row => row.Id == seed.RomId).ExecuteUpdateAsync(set => set.SetProperty(row => row.PreferredOutputId, seed.Cd.Outputs.Single().Id));
            await db.RepresentationOutputs.Where(row => row.RepresentationId == seed.Source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)null));
            await transaction.CommitAsync();
        }
        var cleanup = new MediaRepresentationCleanup(db);
        (await cleanup.CollectAsync(256)).ShouldBe(1);
        var retained = (await new MediaFactReader(db).ReadAsync([seed.Cd.Id]))[seed.Cd.Id];
        retained.Creations.Single().Sources.Single().VerificationId.ShouldBe(seed.Source.Verifications.Single().Id);
        retained.CdBootInspections.Count.ShouldBe(1);
        await db.RomFiles.Where(row => row.Id == seed.RomId).ExecuteDeleteAsync();
        (await cleanup.CollectAsync(256)).ShouldBe(1);
        (await cleanup.CollectAsync(256)).ShouldBe(0);
        (await new MediaFactReader(db).ReadAsync([seed.Cd.Id])).ShouldBeEmpty();
        (await db.Files.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Boot_NewNativeProof_ReusesRetainedInspectorWitnessAndRetainsBothProofs()
    {
        await using var db = _database.CreateContext();
        var seed = await MediaFactGraphFixture.CreateAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var writer = new MediaCreationFactWriter(db);
            Guid boot = await writer.StageCdBootAsync(seed.Cd.Id, seed.Cd.Verifications.Single().Id, "fixture-inspector", DateTimeOffset.UnixEpoch, MediaFactGraphFixture.Boot, default);
            var prior = seed.Cd.Verifications.Single();
            var proof = new RomRepresentationVerification(Guid.NewGuid(), prior.ArtifactSha256, prior.ArtifactSize,
                new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('e'), 3)), prior.CheckedProfile, "fixture-verifier-new",
                DateTimeOffset.UnixEpoch.AddTicks(17), prior.Outputs, seed.Cd.Container, prior.ComputedRawSha1, prior.ComputedOverallSha1);
            (await new MediaFactPublisher(db).AppendVerificationAsync(seed.Cd.Id, proof)).ShouldBeTrue();
            (await writer.StageCdBootAsync(seed.Cd.Id, proof.Id, "fixture-inspector", proof.ObservedAt, MediaFactGraphFixture.Boot, default)).ShouldBe(boot);
            await writer.StageCreationAsync(seed.Cd.Id, seed.Creation(boot, proofId: proof.Id), default);
            await transaction.CommitAsync();
        }
        var retained = (await new MediaFactReader(db).ReadAsync([seed.Cd.Id]))[seed.Cd.Id];
        retained.Verifications.Count.ShouldBe(2);
        retained.CdBootInspections.Count.ShouldBe(1);
        retained.Creations.Single().VerificationId.ShouldNotBe(retained.CdBootInspections.Single().VerificationId);
    }

    [Theory]
    [InlineData("missing-proof")]
    [InlineData("managed-proof")]
    [InlineData("root-outside-first-track")]
    [InlineData("executable-outside-first-track")]
    [InlineData("volume-outside-tracks")]
    [InlineData("empty-root")]
    [InlineData("root-before-volume")]
    [InlineData("unaligned-entry")]
    [InlineData("entry-outside-ram")]
    [InlineData("load-outside-ram")]
    [InlineData("incomplete-executable")]
    [InlineData("index-outside-first-track")]
    [InlineData("empty-inspector")]
    [InlineData("negative-time")]
    [InlineData("audio-first")]
    public async Task Boot_DirectSqlIncompleteOrContradictoryObservation_Rejects(string mutation)
    {
        await using var db = _database.CreateContext();
        var seed = await MediaFactGraphFixture.CreateAsync(db, mutation == "audio-first");
        Guid proof = mutation == "missing-proof" ? Guid.NewGuid() : mutation == "managed-proof" ? seed.Source.Verifications.Single().Id : seed.Cd.Verifications.Single().Id;
        long index = mutation == "index-outside-first-track" ? 115 : 0, volume = mutation == "volume-outside-tracks" ? 241 : 200;
        long root = mutation == "root-outside-first-track" ? 119 : mutation == "root-before-volume" ? 16 : 24;
        long rootBytes = mutation == "root-outside-first-track" ? 4096 : mutation == "empty-root" ? 0 : 864;
        long executable = mutation == "executable-outside-first-track" ? 119 : 26, executableBytes = mutation == "incomplete-executable" ? 2048 : 4096;
        long entry = mutation == "unaligned-entry" ? 0x80010001L : mutation == "entry-outside-ram" ? 0x80200000L : 0x80010000L;
        long load = mutation == "load-outside-ram" ? 0x80200000L : 0x80010000L;
        string inspector = mutation == "empty-inspector" ? "" : "fixture-inspector";
        long ticks = mutation == "negative-time" ? -1 : DateTimeOffset.UnixEpoch.UtcTicks;
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."CdBootInspections" VALUES ({Guid.NewGuid()},{seed.Cd.Id},{proof},{inspector},{RomPs1CdBoot.CurrentProfile},{ticks},
                {index},{volume},{root},{rootBytes},'SLUS_123.45',{executable},{executableBytes},{entry},{load},2048)
            """));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }
}
