using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIddict.EntityFrameworkCore;
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
        await context.Database.MigrateAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(ConsumerGrantSql, cancellationToken);
        await VerifySearchDocumentsAsync(context, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(PublishVersionSql, cancellationToken);

        logger.LogInformation(
            "PostgreSQL schema {Schema} is migrated and published at ROMD version {Version}",
            PostgreSqlConfiguration.SchemaName,
            PostgreSqlConfiguration.ExpectedSchemaVersion);
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
                "Populate them with the documented migration tool before starting the worker.");
        }
    }

    // Column-level and table-level grants need the migrated tables to exist, so they cannot be
    // default privileges in deploy/postgres/init. Dev servers without the romd_* roles skip them.
    private const string ConsumerGrantSql = $$"""
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

    private static readonly string PublishVersionSql = $$"""
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
