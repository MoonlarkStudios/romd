using Microsoft.Extensions.Configuration;
using Npgsql;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Readiness;

public sealed class PostgreSqlHangfireSchemaProbe(IConfiguration configuration) : IHangfireSchemaProbe
{
    public async Task<int?> GetPublishedVersionAsync(CancellationToken cancellationToken)
    {
        string connectionString = HangfirePostgreSqlConfiguration.GetRequiredConnectionString(
            configuration,
            HangfirePostgreSqlConfiguration.RuntimeConnectionName);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT version
            FROM {{HangfirePostgreSqlConfiguration.SchemaName}}.romd_schema
            WHERE singleton = TRUE
            """;
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }
}
