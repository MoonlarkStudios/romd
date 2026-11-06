using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Raw trusted-reference custody is immutable even when no logical inspection selects it.</summary>
public sealed class RomDiscMediaReferenceSchemaTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly byte[] Body = [1, 0, 255];

    [Theory]
    [InlineData("length")]
    [InlineData("digest")]
    public async Task DirectSql_BodyBindingMismatch_RefusesWithoutRetainingAnAssertion(string change)
    {
        var record = Reference();
        await using var db = _database.CreateContext();
        long bytes = change == "length" ? Body.Length + 1 : Body.Length;
        var digest = change == "digest" ? MediaFactFixtures.Identity(new byte[] { 2, 0, 255 }).Sha256 : record.RawReferenceSha256;
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO romd."RomDiscMediaReferences" ("Id","System","Media","FileCount","LogicalBytes","Sha1","Md5","Crc32",
                "RawReferenceBytes","RawReferenceSha256","RawBody","ReferenceSource","ParserProfile","AcceptedSourceProvenance","RetrievedAtUtcTicks")
            VALUES ({record.Id},'PlayStation2','Dvd5',{record.FileCount},{record.LogicalBytes},{record.Sha1.ToArray()},{record.Md5.ToArray()},
                {(long)record.Crc32.ToUInt32()},{bytes},{digest.ToArray()},{Body},{record.ReferenceSource},{record.ParserProfile},
                {record.AcceptedSourceProvenance},{record.RetrievedAt.UtcTicks})
            """));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await db.RomDiscMediaReferences.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("body")]
    [InlineData("delete")]
    [InlineData("profile")]
    [InlineData("time")]
    [InlineData("media")]
    public async Task DirectSql_UnselectedAssertionMutation_RejectsAndRetainsEveryOriginalFact(string change)
    {
        var record = Reference();
        await using var db = _database.CreateContext();
        await using (var publication = await db.Database.BeginTransactionAsync())
        {
            var store = new RomDiscMediaReferenceImportStore(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System));
            (await store.StageAsync(record, Body)).IsError.ShouldBeFalse();
            await publication.CommitAsync();
        }
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        byte[] other = [2, 0, 255];
        FormattableString sql = change switch
        {
            "source" => $"UPDATE romd.\"RomDiscMediaReferences\" SET \"ReferenceSource\"='other-source' WHERE \"Id\"={record.Id}",
            "body" => $"""
                UPDATE romd."RomDiscMediaReferences" SET "RawBody"={other},"RawReferenceBytes"={other.LongLength},
                    "RawReferenceSha256"={MediaFactFixtures.Identity(other).Sha256.ToArray()} WHERE "Id"={record.Id}
                """,
            "delete" => $"DELETE FROM romd.\"RomDiscMediaReferences\" WHERE \"Id\"={record.Id}",
            "profile" => $"UPDATE romd.\"RomDiscMediaReferences\" SET \"ParserProfile\"='other-profile/v1' WHERE \"Id\"={record.Id}",
            "time" => $"UPDATE romd.\"RomDiscMediaReferences\" SET \"RetrievedAtUtcTicks\"=\"RetrievedAtUtcTicks\"+1 WHERE \"Id\"={record.Id}",
            "media" => $"UPDATE romd.\"RomDiscMediaReferences\" SET \"Media\"='Cd' WHERE \"Id\"={record.Id}",
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var retained = await db.RomDiscMediaReferences.SingleAsync();
        retained.ToDomain().ShouldBe(record);
        retained.GetRawBodySnapshot().ShouldBe(Body);
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
    }

    private static RomDiscMediaReferenceRecord Reference()
    {
        var logical = MediaFactFixtures.Identity(new byte[131072]);
        var body = MediaFactFixtures.Identity(Body);
        return new(Guid.NewGuid(), RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Dvd5, 1,
            logical.Size, logical.Sha1, logical.Md5, logical.Crc32, body.Size, body.Sha256,
            "synthetic-reference", "fixture-parser/v1", "trusted-fixture/v1", DateTimeOffset.UnixEpoch.AddTicks(-1));
    }
    public void Dispose() => _database.Dispose();
}
