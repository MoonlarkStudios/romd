using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PspLogicalObservationTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Migration_ExistingBaselineObservation_PreservesHistoryWithoutInventingPspFacts()
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        await db.Database.GetService<IMigrator>().MigrateAsync("20261031050000_FreshMediaBaseline");
        var seed = await LogicalInspectionFixture.PublishAsync(db, DateTimeOffset.UnixEpoch, retain: false);
        var proof = LogicalInspectionFixture.Receipt(seed.Original);
        Guid observation = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."LogicalInspections" ("Id","RomId","SourceOutputId","SourceVerificationId","Profile","InspectorBuild",
                "ObservedAtUtcTicks","Prefix","HasPs2Boot")
            VALUES ({observation},{seed.RomId},{proof.OutputId},{proof.VerificationId},'retained-profile','retained-inspector',
                {DateTimeOffset.UnixEpoch.UtcTicks},{new byte[32]},false)
            """);

        await database.ProvisionAsync();

        var content = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(seed.RomId)).ShouldNotBeNull();
        var retained = content.Inspections.ShouldHaveSingleItem();
        retained.Id.ShouldBe(observation);
        retained.Profile.ShouldBe("retained-profile");
        retained.InspectorBuild.ShouldBe("retained-inspector");
        retained.Identity.ShouldBe(LogicalInspectionFixture.Identity);
        retained.Layout.ShouldBeNull();
        (await new MediaFactReader(db).ReadAsync([seed.Original.Id]))[seed.Original.Id].Verifications.ShouldHaveSingleItem();
        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observation_OriginalOrChd_CommitsExactFactsAndResolvesOnlyPsp(bool chd)
    {
        RomPspLayoutFacts.Profile.ShouldBe(Romd.Media.PlayStation.PspUmdIdentifier.Profile);
        RomPspLayoutFacts.MaximumLogicalBytes.ShouldBe(Romd.Media.PlayStation.PspUmdIdentifier.MaximumLogicalBytes);
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, DateTimeOffset.UnixEpoch, retain: false);
        var source = chd ? seed.Chd : seed.Original;
        var identity = LogicalInspectionFixture.Identity;
        var facts = new RomPspLayoutFacts(identity.Size, 64, "ULUS12345", "1.00");
        var observation = new RomLogicalInspection(Guid.NewGuid(), identity, RomPspLayoutFacts.Profile, "psp-inspector",
            DateTimeOffset.UnixEpoch, new byte[32], layout: facts);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var store = new RomMediaObservationStore(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System));
            (await store.StageAsync(seed.RomId, LogicalInspectionFixture.Receipt(source), observation)).IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var reader = _database.CreateContext();
        var content = (await MediaTestServices.CreateContentCatalog(reader).FindByRomIdAsync(seed.RomId)).ShouldNotBeNull();
        content.Inspections.ShouldHaveSingleItem().SameObservation(observation).ShouldBeTrue();
        var policy = new PspUmdMediaTypePolicy("psp");
        policy.MatchesFacts(content).ShouldBeTrue();
        policy.IsEncodingEligible(content, default).ShouldBeTrue();
        new ChdDvdMediaTypePolicy("ps2").IsEncodingEligible(content, default).ShouldBeFalse();
        new ChdDvdMediaTypePolicy("ps2").MatchesFacts(content).ShouldBeFalse();
        var composition = RomMediaPolicyComposition.Create();
        Romd.Application.Common.Content.MediaKindResolver.Resolve(composition.Kinds, content, [])
            .Unique.ShouldNotBeNull().Kind.ShouldBe("psp-umd");
        composition.Kinds.Find("psp-umd").ShouldNotBeNull().NativeRuntimes.ShouldBeEmpty();
        composition.WholeEncoding["psp-umd"].DiscKind.ShouldBe(RomIsoDiscKind.PlayStationPortableUmd);
    }

    [Fact]
    public async Task Observation_MissingFilesystemFacts_DoesNotAuthorizeEncodingFromAPlatformOrIsoName()
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, DateTimeOffset.UnixEpoch, retain: false);
        var content = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(seed.RomId)).ShouldNotBeNull();
        var policy = new PspUmdMediaTypePolicy("psp");
        policy.MatchesFacts(content).ShouldBeFalse();
        policy.IsEncodingEligible(content, default).ShouldBeFalse();
        policy.EncodingProfile.GetMaximumOutputBytes((2L << 30) + 2048, 10L << 30).ShouldBe(0);
    }

    [Theory]
    [InlineData("volume")]
    [InlineData("missing-id")]
    [InlineData("missing-volume")]
    [InlineData("identifier")]
    [InlineData("version")]
    [InlineData("nintendo")]
    public async Task Observation_DirectSqlContradiction_RejectsAtomically(string change)
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, DateTimeOffset.UnixEpoch, retain: false);
        var proof = LogicalInspectionFixture.Receipt(seed.Original);
        long? blocks = change == "missing-volume" ? null : change == "volume" ? 65 : 64;
        string? id = change == "missing-id" ? null : change == "identifier" ? "ULUS1234X" : "ULUS12345";
        string version = change == "version" ? "a.bb" : "1.00";
        int? nintendo = change == "nintendo" ? 0 : null;
        var exception = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."LogicalInspections" ("Id","RomId","SourceOutputId","SourceVerificationId","Profile","InspectorBuild",
                    "ObservedAtUtcTicks","Prefix","NintendoPartitions","StandardKeyPartitions","KoreanKeyPartitions","HasPs2Boot",
                    "PspVolumeBlocks","PspDiscId","PspDiscVersion")
                VALUES ({Guid.NewGuid()},{seed.RomId},{proof.OutputId},{proof.VerificationId},'psp-iso-umd-game/v1','psp-inspector',
                    {DateTimeOffset.UnixEpoch.UtcTicks},{new byte[32]},CAST({nintendo} AS integer),CAST({nintendo} AS integer),CAST({nintendo} AS integer),false,
                    CAST({blocks} AS bigint),{id},{version})
                """);
            await transaction.CommitAsync();
        });
        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await db.Database.SqlQuery<int>($"""SELECT count(*)::integer AS "Value" FROM romd."LogicalInspections" """).SingleAsync()).ShouldBe(0);
    }
}
