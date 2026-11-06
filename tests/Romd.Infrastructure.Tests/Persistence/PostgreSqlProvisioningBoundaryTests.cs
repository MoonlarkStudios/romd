using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PostgreSqlProvisioningBoundaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProvisionAsync_UnversionedRomdSchema_RefusesBeforeAnyWrites(bool retainHistoryTable)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.Files.Add(new FileEntityPersistence
        {
            Id = 1,
            Sha256 = Sha256.Parse(new string('a', 64)),
            Size = 7,
            SizeOnDisk = 7
        });
        await db.SaveChangesAsync();
        if (retainHistoryTable)
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM romd."__EFMigrationsHistory";""");
        else
            await db.Database.ExecuteSqlRawAsync("""DROP TABLE romd."__EFMigrationsHistory";""");
        string relationsBefore = await SchemaObjectsAsync(db);
        string fileBefore = await db.Database.SqlQueryRaw<string>(
            """SELECT to_jsonb(f)::text AS "Value" FROM romd."Files" f""").SingleAsync();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => database.ProvisionAsync());

        error.Message.ShouldBe("This worker requires a new empty application database or this release's fresh baseline. No schema changes were applied.");
        (await SchemaObjectsAsync(db)).ShouldBe(relationsBefore);
        (await db.Database.SqlQueryRaw<string>(
            """SELECT to_jsonb(f)::text AS "Value" FROM romd."Files" f""").SingleAsync()).ShouldBe(fileBefore);
        (await db.Database.GetAppliedMigrationsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("CREATE SEQUENCE romd.legacy_sequence")]
    [InlineData("CREATE VIEW romd.legacy_view AS SELECT 1 AS id")]
    [InlineData("CREATE TYPE romd.legacy_type AS ENUM ('legacy')")]
    [InlineData("CREATE FUNCTION romd.legacy_function() RETURNS integer LANGUAGE SQL AS 'SELECT 1'")]
    public async Task ProvisionAsync_PartialUnversionedSchema_RefusesBeforeAnyWrites(string legacyObjectSql)
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA romd");
        await db.Database.ExecuteSqlRawAsync(legacyObjectSql);
        string relationsBefore = await SchemaObjectsAsync(db);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => database.ProvisionAsync());

        error.Message.ShouldBe("This worker requires a new empty application database or this release's fresh baseline. No schema changes were applied.");
        (await SchemaObjectsAsync(db)).ShouldBe(relationsBefore);
        (await db.Database.GetAppliedMigrationsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisionAsync_FreshDatabase_AppliesMigrationsAndPublishesVersion(bool retainEmptyHistory)
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        if (retainEmptyHistory)
            await db.Database.ExecuteSqlRawAsync("""
                CREATE SCHEMA romd;
                CREATE TABLE romd."__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                """);
        // Objects outside ROMD's schema belong to other applications and do not block a fresh install.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE public.unrelated (id integer PRIMARY KEY)");

        await database.ProvisionAsync();

        (await db.Database.GetAppliedMigrationsAsync()).ShouldBe(db.Database.GetMigrations());
        (await db.Database.SqlQueryRaw<int>(
            """SELECT version AS "Value" FROM romd.romd_schema WHERE singleton""").SingleAsync())
            .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
    }

    [Fact]
    public async Task ProvisionAsync_CurrentDatabase_PreservesDataAndPublishesVersion()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.Files.Add(new FileEntityPersistence
        {
            Id = 1,
            Sha256 = Sha256.Parse(new string('b', 64)),
            Size = 11,
            SizeOnDisk = 11
        });
        await db.SaveChangesAsync();

        await database.ProvisionAsync();

        (await db.Database.GetAppliedMigrationsAsync()).ShouldBe(db.Database.GetMigrations());
        (await db.Database.SqlQueryRaw<int>(
            """SELECT version AS "Value" FROM romd.romd_schema WHERE singleton""").SingleAsync())
            .ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
        (await db.Files.SingleAsync()).Size.ShouldBe(11);
    }

    private static Task<string> SchemaObjectsAsync(RomdDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT coalesce(string_agg(object, ',' ORDER BY object), '') AS "Value" FROM (
            SELECT 'relation:' || c.relname || ':' || c.oid::text || ':' || c.relkind::text AS object
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'romd'
            UNION ALL
            SELECT 'function:' || p.oid::text || ':' || pg_get_functiondef(p.oid)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'romd'
            UNION ALL
            SELECT 'type:' || to_jsonb(t)::text
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = 'romd'
        ) objects
        """).SingleAsync();
}
