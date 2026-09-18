using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Readiness;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class DatabaseReadinessCheckTests
{
    [Fact]
    public async Task EvaluateAsync_MigratedDatabase_ReturnsHealthy()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var check = new DatabaseReadinessCheck(db);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_PendingMigrations_ReturnsUnready()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        // The schema exists but the history says nothing was applied: the baseline is pending.
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM {PostgreSqlConfiguration.SchemaName}.\"{PostgreSqlConfiguration.MigrationsHistoryTable}\"");
        var check = new DatabaseReadinessCheck(db);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Unready);
    }

    [Fact]
    public async Task EvaluateAsync_UnreachableServer_ReturnsUnready()
    {
        await using var db = new RomdDbContext(new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=romd;Username=romd;Timeout=1")
            .UseOpenIddict()
            .Options);
        var check = new DatabaseReadinessCheck(db);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Unready);
    }
}
