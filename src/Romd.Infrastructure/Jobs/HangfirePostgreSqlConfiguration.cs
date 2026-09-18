using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;

namespace Romd.Infrastructure.Jobs;

public static class HangfirePostgreSqlConfiguration
{
    public const string SchemaName = "hangfire";
    public const string RuntimeConnectionName = "Hangfire";
    public const string ProvisioningConnectionName = "HangfireProvisioning";
    public const int ExpectedSchemaVersion = 1;

    public static string GetRequiredConnectionString(
        IConfiguration configuration,
        string connectionName)
    {
        string? connectionString = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{connectionName} must be configured for Hangfire PostgreSQL.");
        }

        return connectionString;
    }

    public static PostgreSqlStorageOptions CreateStorageOptions(bool prepareSchema) => new()
    {
        SchemaName = SchemaName,
        PrepareSchemaIfNecessary = prepareSchema,
        StartupConnectionMaxRetries = prepareSchema ? 5 : 0,
        StartupConnectionBaseDelay = TimeSpan.FromSeconds(1),
        StartupConnectionMaxDelay = TimeSpan.FromSeconds(10),
        AllowDegradedModeWithoutStorage = false,
        UseSlidingInvisibilityTimeout = true,
        InvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.FromSeconds(1)
    };
}
