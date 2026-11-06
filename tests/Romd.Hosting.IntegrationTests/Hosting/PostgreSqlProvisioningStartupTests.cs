using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Romd.Hosting;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class PostgreSqlProvisioningStartupTests
{
    [Fact]
    public async Task SchemaProvisioning_UnversionedRomdSchema_RefusesBeforeHangfireWrites()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""DROP TABLE romd."__EFMigrationsHistory";""");
        string before = await SchemaObjectsAsync(db);
        using var host = new HostBuilder()
            .UseEnvironment("Development")
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PostgreSqlConfiguration.ProvisioningConnectionName}"] = database.ConnectionString,
                [$"ConnectionStrings:{HangfirePostgreSqlConfiguration.ProvisioningConnectionName}"] = database.ConnectionString
            }))
            // Exercise the same provisioning registration and sequential host startup as the worker.
            .ConfigureServices((context, services) => services.AddRomdSchemaProvisioning(context.HostingEnvironment))
            .Build();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());

        error.Message.ShouldBe("This worker requires a new empty application database or this release's fresh baseline. " +
            "No schema changes were applied.");
        (await SchemaObjectsAsync(db)).ShouldBe(before);
    }

    private static Task<string> SchemaObjectsAsync(RomdDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT coalesce(string_agg(n.nspname || ':' || c.relname || ':' || c.oid::text, ',' ORDER BY n.nspname, c.relname), '') AS "Value"
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('romd', 'hangfire')
        """).SingleAsync();
}
