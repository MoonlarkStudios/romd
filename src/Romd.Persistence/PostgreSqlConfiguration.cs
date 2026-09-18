using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Romd.Persistence;

/// <summary>
///     Application-database PostgreSQL contract (docs/decisions/postgresql-single-provider-cutover.md):
///     the <c>romd</c> schema shares one database with Hangfire, runtime hosts connect with
///     per-role credentials, and only the worker's provisioning credential migrates the schema
///     and publishes the version API hosts require for readiness.
/// </summary>
public static class PostgreSqlConfiguration
{
    public const string SchemaName = "romd";
    public const string RuntimeConnectionName = "Romd";
    public const string ProvisioningConnectionName = "RomdProvisioning";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";
    public const string SchemaVersionTable = "romd_schema";
    public const int ExpectedSchemaVersion = 32;
    private const int MaxBatchSize = 1000;

    public static string GetRequiredConnectionString(IConfiguration configuration, string connectionName)
    {
        string? connectionString = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{connectionName} must be configured for the ROMD PostgreSQL database.");
        }

        return connectionString;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable(MigrationsHistoryTable, SchemaName)
            .MaxBatchSize(MaxBatchSize));
}
