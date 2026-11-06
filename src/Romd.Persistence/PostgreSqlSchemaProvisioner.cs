using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIddict.EntityFrameworkCore;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Search;

namespace Romd.Persistence;

/// <summary>
///     Worker-only owner of the <c>romd</c> schema. Runs the EF migrations under the provisioning
///     credential (which assumes <c>romd_owner</c>), grants the consumer role exactly the writes
///     <c>ConsumerWriteGuardInterceptor</c> permits, verifies every searchable row carries its search
///     document, and then publishes the schema version that API hosts require before they report
///     ready. API hosts never call this; their credentials cannot create or alter objects.
/// </summary>
public sealed class PostgreSqlSchemaProvisioner(
    IConfiguration configuration,
    ILogger<PostgreSqlSchemaProvisioner> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string connectionString = PostgreSqlConfiguration.GetRequiredConnectionString(
            configuration,
            PostgreSqlConfiguration.ProvisioningConnectionName);
        var options = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(options, connectionString);
        options.UseOpenIddict();

        await using var context = new RomdDbContext(options.Options);
        await RefuseUnknownMigrationsAsync(context, cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(_consumerGrantSql, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(_discReferenceGrantSql, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(MediaFactSchema.RuntimeGrantsSql, cancellationToken);
        await VerifySearchDocumentsAsync(context, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(_publishVersionSql, cancellationToken);

        logger.LogInformation(
            "PostgreSQL schema {Schema} is migrated and published at ROMD version {Version}",
            PostgreSqlConfiguration.SchemaName,
            PostgreSqlConfiguration.ExpectedSchemaVersion);
    }

    public static async Task RefuseUnknownMigrationsAsync(RomdDbContext context, CancellationToken ct = default)
    {
        var known = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        string[] applied = (await context.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        var unknown = applied.Where(id => !known.Contains(id)).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException($"This worker cannot provision an unsupported ROMD schema: {string.Join(", ", unknown)}.");

        // GetAppliedMigrations is read-only even when the history table is absent. Check before
        // MigrateAsync: EF can otherwise create history before discovering conflicting legacy objects.
        if (applied.Length == 0 && await context.Database.SqlQuery<bool>($"""
                SELECT (
                    EXISTS (
                        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = {PostgreSqlConfiguration.SchemaName}
                            AND c.relkind NOT IN ('i', 'I')
                            AND c.relname <> {PostgreSqlConfiguration.MigrationsHistoryTable})
                    OR EXISTS (
                        SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                        WHERE n.nspname = {PostgreSqlConfiguration.SchemaName})
                    OR EXISTS (
                        SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                        WHERE n.nspname = {PostgreSqlConfiguration.SchemaName}
                            AND t.typrelid = 0 AND t.typelem = 0)
                ) AS "Value"
                """).SingleAsync(ct))
            throw new InvalidOperationException(
                "This worker requires a new empty application database or this release's fresh baseline. " +
                "No schema changes were applied.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task VerifySearchDocumentsAsync(RomdDbContext context, CancellationToken cancellationToken)
    {
        int unpopulatedTitles = await context.Titles
            .CountAsync(
                title => title.SearchDocument == string.Empty && title.Name != string.Empty,
                cancellationToken);
        int unpopulatedGames = await context.DatGames
            .CountAsync(game => game.SearchDocument == string.Empty && game.Name != string.Empty, cancellationToken);
        if (unpopulatedTitles > 0 || unpopulatedGames > 0)
        {
            throw new InvalidOperationException(
                $"{unpopulatedTitles} titles and {unpopulatedGames} DAT games have no search document. " +
                "Search documents must be supplied when new titles and DAT games are created.");
        }
    }

    // Column-level and table-level grants need the migrated tables to exist, so they cannot be
    // default privileges in deploy/postgres/init. Dev servers without the romd_* roles skip them.
    private const string _consumerGrantSql = $$"""
        DO $consumer$
        BEGIN
            IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_consumer') THEN
                RETURN;
            END IF;

            -- Provider credentials are management-only, including their encrypted representation.
            -- Revoke the schema-wide default SELECT grant before API readiness is published.
            REVOKE ALL PRIVILEGES ON {{PostgreSqlConfiguration.SchemaName}}."MetadataProviderSettings"
                FROM romd_consumer;

            REVOKE ALL PRIVILEGES ON {{PostgreSqlConfiguration.SchemaName}}."AccountLinks",
                {{PostgreSqlConfiguration.SchemaName}}."AdminAuditEvents" FROM romd_consumer;

            GRANT INSERT, UPDATE ON {{PostgreSqlConfiguration.SchemaName}}."AccountSessions" TO romd_consumer;
            GRANT INSERT, UPDATE ON {{PostgreSqlConfiguration.SchemaName}}."VfsConnections" TO romd_consumer;
            GRANT INSERT, UPDATE ON {{PostgreSqlConfiguration.SchemaName}}."ConsumerUserSettings" TO romd_consumer;
            GRANT INSERT, UPDATE, DELETE ON {{PostgreSqlConfiguration.SchemaName}}."PlaySessions" TO romd_consumer;
            GRANT INSERT, UPDATE, DELETE
                ON {{PostgreSqlConfiguration.SchemaName}}."OpenIddictAuthorizations" TO romd_consumer;
            GRANT INSERT, UPDATE, DELETE
                ON {{PostgreSqlConfiguration.SchemaName}}."OpenIddictTokens" TO romd_consumer;
            GRANT UPDATE ("PasswordHash", "SecurityStamp", "ConcurrencyStamp", "LastSignedInAt")
                ON {{PostgreSqlConfiguration.SchemaName}}."AspNetUsers" TO romd_consumer;
        END
        $consumer$;
        """;

    // Retained source bodies are admitted only by the worker. Schema-wide defaults must not
    // let either API host fabricate or rewrite the evidence used by PS2 DVD eligibility.
    private const string _discReferenceGrantSql = $$"""
        REVOKE ALL PRIVILEGES ON {{PostgreSqlConfiguration.SchemaName}}."RomDiscMediaReferences" FROM PUBLIC;
        DO $disc_reference$
        BEGIN
            IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_admin') THEN
                REVOKE ALL PRIVILEGES ON {{PostgreSqlConfiguration.SchemaName}}."RomDiscMediaReferences" FROM romd_admin;
            END IF;
            IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_consumer') THEN
                REVOKE ALL PRIVILEGES ON {{PostgreSqlConfiguration.SchemaName}}."RomDiscMediaReferences" FROM romd_consumer;
            END IF;
            IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_worker') THEN
                REVOKE ALL PRIVILEGES ON {{PostgreSqlConfiguration.SchemaName}}."RomDiscMediaReferences" FROM romd_worker;
                GRANT SELECT, INSERT ON {{PostgreSqlConfiguration.SchemaName}}."RomDiscMediaReferences" TO romd_worker;
                -- PostgreSQL row locks require UPDATE on at least one column. Grant only Id;
                -- the unconditional immutable-reference trigger still rejects every row update.
                GRANT UPDATE ("Id") ON {{PostgreSqlConfiguration.SchemaName}}."RomDiscMediaReferences" TO romd_worker;
            END IF;
        END
        $disc_reference$;
        """;

    private static readonly string _publishVersionSql = $$"""
        CREATE TABLE IF NOT EXISTS
            {{PostgreSqlConfiguration.SchemaName}}.{{PostgreSqlConfiguration.SchemaVersionTable}} (
            singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton),
            version integer NOT NULL CHECK (version > 0),
            published_at_utc timestamp with time zone NOT NULL
        );

        INSERT INTO {{PostgreSqlConfiguration.SchemaName}}.{{PostgreSqlConfiguration.SchemaVersionTable}}
            (singleton, version, published_at_utc)
        VALUES (TRUE, {{PostgreSqlConfiguration.ExpectedSchemaVersion}}, CURRENT_TIMESTAMP)
        ON CONFLICT (singleton) DO UPDATE
        SET version = EXCLUDED.version,
            published_at_utc = EXCLUDED.published_at_utc;

        DO $grant$
        BEGIN
            IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_consumer') THEN
                GRANT SELECT ON {{PostgreSqlConfiguration.SchemaName}}.{{PostgreSqlConfiguration.SchemaVersionTable}}
                    TO romd_consumer;
            END IF;

            -- The version row is the API hosts' readiness gate and the history table drives the
            -- next migration run: only the provisioning role may rewrite either, so the DML the
            -- role script grants runtime roles by default is taken back here.
            IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_worker') THEN
                REVOKE INSERT, UPDATE, DELETE
                    ON {{PostgreSqlConfiguration.SchemaName}}.{{PostgreSqlConfiguration.SchemaVersionTable}},
                       {{PostgreSqlConfiguration.SchemaName}}."{{PostgreSqlConfiguration.MigrationsHistoryTable}}"
                    FROM romd_worker;
            END IF;
            IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_admin') THEN
                REVOKE INSERT, UPDATE, DELETE
                    ON {{PostgreSqlConfiguration.SchemaName}}.{{PostgreSqlConfiguration.SchemaVersionTable}},
                       {{PostgreSqlConfiguration.SchemaName}}."{{PostgreSqlConfiguration.MigrationsHistoryTable}}"
                    FROM romd_admin;
            END IF;
        END
        $grant$;
        """;
}
