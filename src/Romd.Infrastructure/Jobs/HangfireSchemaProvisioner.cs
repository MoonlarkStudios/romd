using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Romd.Infrastructure.Jobs;

/// <summary>
///     Worker-only startup owner for the Hangfire PostgreSQL schema. The storage used by
///     Hangfire servers has schema preparation disabled and uses a separate runtime credential.
/// </summary>
public sealed class HangfireSchemaProvisioner(
    IConfiguration configuration,
    ILogger<HangfireSchemaProvisioner> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string connectionString = HangfirePostgreSqlConfiguration.GetRequiredConnectionString(
            configuration,
            HangfirePostgreSqlConfiguration.ProvisioningConnectionName);
        var storageOptions = HangfirePostgreSqlConfiguration.CreateStorageOptions(prepareSchema: true);
        var connectionFactory = new NpgsqlConnectionFactory(connectionString, storageOptions, null);

        // Construction runs the provider's serialized install/upgrade path. No API host creates
        // this storage instance and the runtime storage always has PrepareSchemaIfNecessary=false.
        _ = new PostgreSqlStorage(connectionFactory, storageOptions);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE IF NOT EXISTS {{HangfirePostgreSqlConfiguration.SchemaName}}.romd_schema (
                singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton),
                version integer NOT NULL CHECK (version > 0),
                published_at_utc timestamp with time zone NOT NULL
            );

            INSERT INTO {{HangfirePostgreSqlConfiguration.SchemaName}}.romd_schema
                (singleton, version, published_at_utc)
            VALUES (TRUE, @version, CURRENT_TIMESTAMP)
            ON CONFLICT (singleton) DO UPDATE
            SET version = EXCLUDED.version,
                published_at_utc = EXCLUDED.published_at_utc;

            DO $grant$
            BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_consumer') THEN
                    GRANT SELECT ON {{HangfirePostgreSqlConfiguration.SchemaName}}.romd_schema
                        TO romd_consumer;
                END IF;

                -- The version row is the API hosts' readiness gate: only the provisioning role may
                -- rewrite it, so the DML the role script grants runtime roles by default is taken back.
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_worker') THEN
                    REVOKE INSERT, UPDATE, DELETE ON {{HangfirePostgreSqlConfiguration.SchemaName}}.romd_schema
                        FROM romd_worker;
                END IF;
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'romd_admin') THEN
                    REVOKE INSERT, UPDATE, DELETE ON {{HangfirePostgreSqlConfiguration.SchemaName}}.romd_schema
                        FROM romd_admin;
                END IF;
            END
            $grant$;
            """;
        command.Parameters.AddWithValue("version", HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation(
            "Hangfire PostgreSQL schema {Schema} is ready at ROMD version {Version}",
            HangfirePostgreSqlConfiguration.SchemaName,
            HangfirePostgreSqlConfiguration.ExpectedSchemaVersion);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
