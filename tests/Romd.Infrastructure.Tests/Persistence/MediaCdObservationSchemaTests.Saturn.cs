using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaCdObservationSchemaTests
{
    [Fact]
    public async Task Saturn_VerifiedObservationAndCreation_RoundTripWithoutPs1Evidence()
    {
        await using var db = _database.CreateContext();
        var seed = await MediaFactGraphFixture.CreateAsync(db, mode1: true);
        var boot = new RomSaturnCdBoot(RomSaturnCdBoot.CurrentProfile, 0, "T-00001", "JUE", 4096, MediaFactGraphFixture.Hash('b'));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var writer = new MediaCreationFactWriter(db);
            Guid id = await writer.StageCdBootAsync(seed.Cd.Id, seed.Cd.Verifications.Single().Id, "saturn-inspector", DateTimeOffset.UnixEpoch, boot, default);
            (await writer.StageCdBootAsync(seed.Cd.Id, seed.Cd.Verifications.Single().Id, "saturn-inspector", DateTimeOffset.UnixEpoch.AddSeconds(1), boot, default)).ShouldBe(id);
            await writer.StageCreationAsync(seed.Cd.Id, seed.Creation(id), default);
            await transaction.CommitAsync();
        }
        var retained = (await new MediaFactReader(db).ReadAsync([seed.Cd.Id]))[seed.Cd.Id];
        retained.CdBootInspections.ShouldHaveSingleItem().Boot.ShouldBe(boot);
        retained.Creations.ShouldHaveSingleItem().Cd!.BootInspectionId.ShouldBe(retained.CdBootInspections.Single().Id);
    }

    [Theory]
    [InlineData("ps1-fields")]
    [InlineData("missing-header")]
    [InlineData("ip-overrun")]
    [InlineData("wrong-mode")]
    [InlineData("wrong-family")]
    [InlineData("missing-proof")]
    public async Task Saturn_IncompleteContradictoryOrUnmeasuredFacts_AreRefusedByPostgreSql(string mutation)
    {
        await using var db = _database.CreateContext();
        var seed = await MediaFactGraphFixture.CreateAsync(db, mode1: mutation != "wrong-mode");
        byte[]? header = mutation == "missing-header" ? null : MediaFactGraphFixture.Hash('b').ToArray();
        string profile = mutation == "wrong-family" ? RomPs1CdBoot.CurrentProfile : RomSaturnCdBoot.CurrentProfile;
        string? path = mutation == "ps1-fields" ? "PSX.EXE" : null;
        long index = mutation == "ip-overrun" ? 119 : 0;
        Guid proof = mutation == "missing-proof" ? Guid.NewGuid() : seed.Cd.Verifications.Single().Id;
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."CdBootInspections" ("Id","RepresentationId","VerificationId","InspectorBuild","Profile","ObservedAtUtcTicks","FirstTrackIndex01",
                "SaturnProductCode","SaturnAreaSymbols","SaturnInitialProgramBytes","SaturnHeaderSha256","BootPath")
            VALUES ({Guid.NewGuid()},{seed.Cd.Id},{proof},'saturn-inspector',{profile},{DateTimeOffset.UnixEpoch.UtcTicks},{index},
                'T-00001','JUE',4096,CAST({header} AS bytea),CAST({path} AS text))
            """));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }
}
