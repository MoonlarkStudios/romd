using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Readiness;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class AdminRealtimeOutboxReadinessCheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;
    private readonly ManualTimeProvider _timeProvider = new(Now);

    public AdminRealtimeOutboxReadinessCheckTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = new RomdDbContext(_contextOptions);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task EvaluateAsync_NoOutboxRows_ReturnsHealthy()
    {
        await using var db = new RomdDbContext(_contextOptions);
        var check = CreateCheck(db);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_FreshUnprocessedRow_ReturnsHealthy()
    {
        await using var db = new RomdDbContext(_contextOptions);
        await SeedRowAsync(db, createdAtUtc: Now.AddMinutes(-1), processedAtUtc: null);
        var check = CreateCheck(db);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_UnprocessedRowOlderThanThreshold_ReturnsDegraded()
    {
        await using var db = new RomdDbContext(_contextOptions);
        await SeedRowAsync(db, createdAtUtc: Now.AddMinutes(-6), processedAtUtc: null);
        var check = CreateCheck(db);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Degraded);
    }

    [Fact]
    public async Task EvaluateAsync_StaleRowAlreadyProcessed_ReturnsHealthy()
    {
        await using var db = new RomdDbContext(_contextOptions);
        await SeedRowAsync(db, createdAtUtc: Now.AddMinutes(-6), processedAtUtc: Now.AddMinutes(-5));
        var check = CreateCheck(db);

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Healthy);
    }

    private AdminRealtimeOutboxReadinessCheck CreateCheck(RomdDbContext db) =>
        new(db, _timeProvider, new ReadinessOptions());

    private static async Task SeedRowAsync(
        RomdDbContext db,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? processedAtUtc)
    {
        db.AdminRealtimeOutboxEvents.Add(new AdminRealtimeOutboxEventEntity
        {
            EventType = "JobUpdated",
            PayloadJson = "{}",
            CreatedAtUtc = createdAtUtc,
            AvailableAtUtc = createdAtUtc,
            ProcessedAtUtc = processedAtUtc
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
