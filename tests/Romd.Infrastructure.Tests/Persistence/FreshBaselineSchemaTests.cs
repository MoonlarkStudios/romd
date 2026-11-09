using Microsoft.EntityFrameworkCore;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class FreshBaselineSchemaTests
{
    [Fact]
    public async Task Provision_EmptyDatabaseAndRepeatedStartup_HasImmutableBaselineAndAdditivePspMigration()
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await database.ProvisionAsync();
        await database.ProvisionAsync();
        await using var db = database.CreateContext();
        (await db.Database.GetAppliedMigrationsAsync()).ShouldBe(["20261031050000_FreshMediaBaseline", "20261102000000_PspUmdObservations", "20261109000000_SaturnCdObservations"]);
        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        db.Database.HasPendingModelChanges().ShouldBeFalse();
        PostgreSqlConfiguration.ExpectedSchemaVersion.ShouldBe(74);
        (await db.Database.SqlQueryRaw<int>("SELECT version AS \"Value\" FROM romd.romd_schema").SingleAsync()).ShouldBe(74);
        (await db.Database.SqlQueryRaw<string>("""
            SELECT c.relname AS "Value" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'romd' AND c.relname IN
                ('RomFiles','RomRepresentations','RomVerifications','RomChdIntegrities','RomPs2BootEvidence','RomCdFileSets',
                    'ConvergenceAttempts','RomConvergenceSources','RomConvergenceOutcomes','RomConvergenceBulkItems',
                    'RomCdConvergenceSources','RomCdConvergenceIntents','RomChdReverificationSources','RomChdReverificationItems',
                    'RomPs2ReferenceReevaluationRequests')
            """).ToArrayAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("20260903203657_PostgreSqlBaseline")]
    [InlineData("20261030010000_FreshMediaBaseline")]
    [InlineData("20261030020000_FreshMediaBaseline")]
    [InlineData("20261030030000_FreshMediaBaseline")]
    [InlineData("20261030040000_FreshMediaBaseline")]
    public async Task Provision_UnsupportedReceipt_RefusesWithoutChangingObjectsDataOrHistory(string receipt)
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA romd;
            CREATE TABLE public."KeepMe" ("Value" text NOT NULL);
            INSERT INTO public."KeepMe" VALUES ('unchanged');
            CREATE TABLE romd."__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY,"ProductVersion" varchar(32) NOT NULL);
            INSERT INTO romd."__EFMigrationsHistory" VALUES ('20260903203657_PostgreSqlBaseline','10.0.13');
            """);
        await db.Database.ExecuteSqlAsync($"UPDATE romd.\"__EFMigrationsHistory\" SET \"MigrationId\" = {receipt}");
        string before = await SnapshotAsync(db);
        var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var error = await Should.ThrowAsync<InvalidOperationException>(() => database.ProvisionAsync());
        error.Message.ShouldContain("unsupported ROMD schema");
        (await SnapshotAsync(db)).ShouldBe(before);
        (await db.Database.GetAppliedMigrationsAsync()).ShouldBe(history);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("function")]
    [InlineData("enum")]
    public async Task Provision_UnversionedApplicationObjects_RefusesBeforeCreatingHistory(string kind)
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA romd;
            CREATE TABLE public."KeepMe" ("Value" text NOT NULL);
            INSERT INTO public."KeepMe" VALUES ('unchanged');
            """);
        await db.Database.ExecuteSqlRawAsync(kind switch
        {
            "table" => "CREATE TABLE romd.legacy (value integer)",
            "function" => "CREATE FUNCTION romd.legacy() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
            "enum" => "CREATE TYPE romd.legacy AS ENUM ('existing')",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
        string before = await SnapshotAsync(db);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => database.ProvisionAsync());
        error.Message.ShouldContain("No schema changes were applied");
        (await SnapshotAsync(db)).ShouldBe(before);
        (await db.Database.GetAppliedMigrationsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task FreshMetadata_HasRequiredTaxonomyKeysNullableItemOutcomeAndCurrentEnrichmentContract()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var requiredKeys = await db.Database.SqlQueryRaw<string>("""
            SELECT table_name AS "Value" FROM information_schema.columns WHERE table_schema = 'romd'
                AND table_name IN ('Regions','GameLanguages') AND column_name = 'CanonicalKey' AND is_nullable = 'NO'
            ORDER BY table_name
            """).ToArrayAsync();
        requiredKeys.ShouldBe(["GameLanguages", "Regions"]);
        (await db.Database.SqlQueryRaw<string>("""
            SELECT concat_ws(' | ',data_type,character_maximum_length,is_nullable) AS "Value" FROM information_schema.columns
            WHERE table_schema = 'romd' AND table_name = 'ArtworkCompletionItems' AND column_name = 'MetadataOutcome'
            """).SingleAsync()).ShouldBe("character varying | 20 | YES");
        var signatures = await db.Database.SqlQueryRaw<string>("""
            SELECT pg_get_function_result(p.oid) AS "Value" FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'romd' AND p.proname = 'enrichment_requirement_facts_set'
            """).ToArrayAsync();
        signatures.ShouldHaveSingleItem().ShouldBe("TABLE(\"TitleId\" integer, \"Kind\" integer, \"Subject\" text, \"Satisfied\" boolean, \"RequiresDecision\" boolean, \"Blocked\" boolean, \"Processing\" boolean, \"Cause\" text, \"SatisfiedBy\" text)");
    }

    private static Task<string> SnapshotAsync(RomdDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT jsonb_build_object(
            'objects',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relkind) ORDER BY c.relname,c.relkind)
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'romd'),
            'functions',(SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.oid::regprocedure::text)
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'romd'),
            'types',(SELECT jsonb_agg(t.typname ORDER BY t.typname)
                FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = 'romd'),
            'data',(SELECT jsonb_agg(to_jsonb(k)) FROM public."KeepMe" k))::text AS "Value"
        """).SingleAsync();
}
