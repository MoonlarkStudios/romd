using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Diagnostics;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Diagnostics;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Diagnostics;

public sealed class OperationalDiagnosticsReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _options;
    private readonly ManualTimeProvider _timeProvider = new(Now);

    public OperationalDiagnosticsReaderTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _options = new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(_connection.ConnectionString).Options;
        using var db = CreateDb();
    }

    [Fact]
    public async Task ReadCatalogProjectionsAsync_OverLimit_ReturnsSeverityOrderedBoundedRows()
    {
        await using var db = CreateDb();
        db.Platforms.AddRange(
            Platform(1, "Clean", CatalogRebuildState.Clean),
            Platform(2, "Dirty", CatalogRebuildState.Dirty),
            Platform(3, "Failed", CatalogRebuildState.Failed));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new OperationalDiagnosticsReader(db, _timeProvider)
            .ReadCatalogProjectionsAsync(2);

        result.IsTruncated.ShouldBeTrue();
        result.Items.Select(item => item.State).ShouldBe([
            CatalogRebuildState.Failed,
            CatalogRebuildState.Dirty
        ]);
    }

    [Fact]
    public async Task ReadJobsAsync_UsesSweepAndStrandingPredicatesAndBoundsEachKind()
    {
        await using var db = CreateDb();
        var oldestReplace = ReplaceJob(
            Guid.NewGuid(),
            ReplaceDatPhase.Replacing,
            Now.AddMinutes(-40),
            Now.AddHours(-2));
        db.Set<ReplaceDatJobEntity>().AddRange(
            ReplaceJob(Guid.NewGuid(), ReplaceDatPhase.Replacing, Now.AddMinutes(-31), Now.AddHours(-1)),
            oldestReplace,
            ReplaceJob(Guid.NewGuid(), ReplaceDatPhase.Replacing, Now.AddMinutes(-30), Now.AddHours(-1)),
            ReplaceJob(Guid.NewGuid(), ReplaceDatPhase.Completed, Now.AddHours(-1), Now.AddHours(-2)));
        db.Set<ReplaceDatJobEntity>().AddRange(Enumerable.Range(1, 750).Select(index =>
            ReplaceJob(
                Guid.NewGuid(),
                ReplaceDatPhase.Completed,
                Now.AddDays(-index - 1),
                Now.AddDays(-index - 2))));
        db.Set<BulkEnrichmentJobEntity>().AddRange(
            BulkJob(Guid.NewGuid(), Now.AddMinutes(-11), hangfireJobId: null),
            BulkJob(Guid.NewGuid(), Now.AddMinutes(-20), hangfireJobId: null),
            BulkJob(Guid.NewGuid(), Now.AddMinutes(-10), hangfireJobId: null),
            BulkJob(Guid.NewGuid(), Now.AddMinutes(-30), hangfireJobId: "42"));
        db.Set<BulkEnrichmentJobEntity>().AddRange(Enumerable.Range(1, 750).Select(index =>
            BulkJob(Guid.NewGuid(), Now.AddDays(-index - 1), hangfireJobId: $"dispatched-{index}")));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new OperationalDiagnosticsReader(db, _timeProvider).ReadJobsAsync(1);

        result.ReplaceDatJobs.IsTruncated.ShouldBeTrue();
        var replace = result.ReplaceDatJobs.Items.ShouldHaveSingleItem();
        replace.LastProgressAt.ShouldBe(Now.AddMinutes(-40));
        result.BulkEnrichmentJobs.IsTruncated.ShouldBeTrue();
        result.BulkEnrichmentJobs.Items.ShouldHaveSingleItem().CreatedAt.ShouldBe(Now.AddMinutes(-20));
    }

    [Fact]
    public async Task ReadJobsAsync_WedgedReplaceDatJob_ProjectsBoundedRecordedAttemptError()
    {
        await using var db = CreateDb();
        var job = ReplaceDatJob.Create(7, "replacement.dat");
        job.Start("hangfire-1");
        job.RecordError("unrelated", new string('u', 10_000));
        job.RecordError("attempt", new string('a', OperationalDiagnosticsPolicy.AttemptErrorLimit + 1));
        db.Set<ReplaceDatJobEntity>().Add(ReplaceDatJobEntity.FromDomain(job, Now.AddMinutes(-40)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new OperationalDiagnosticsReader(db, _timeProvider).ReadJobsAsync(1);

        var replace = result.ReplaceDatJobs.Items.ShouldHaveSingleItem();
        replace.AttemptError.ShouldBe(new string('a', OperationalDiagnosticsPolicy.AttemptErrorLimit));
        replace.AttemptErrorTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadJobsAsync_BoundedDiagnosticsQueries_HaveIndexesAndUseThem()
    {
        await using var db = CreateDb();

        var replacePlan = await ExplainAsync(db,
            """
            SELECT "Id" FROM romd."Jobs"
            WHERE "JobType" = 'replace_dat'
              AND "Phase" IN ('Ingesting', 'Replacing')
              AND "StartedAt" IS NOT NULL
              AND "UpdatedAt" < 0
            ORDER BY "UpdatedAt", "Id" LIMIT 101
            """);
        var bulkPlan = await ExplainAsync(db,
            """
            SELECT "Id" FROM romd."Jobs"
            WHERE "JobType" = 'bulk_enrichment'
              AND "Phase" = 'Pending'
              AND ("HangfireJobId" IS NULL OR "HangfireJobId" = '')
              AND "CreatedAt" < 0
            ORDER BY "CreatedAt", "Id" LIMIT 101
            """);

        // The planner cannot rank the candidate Jobs indexes on an empty fixture table, so this
        // asserts the diagnostics indexes exist with their bounded column order and that both
        // queries are index-driven; the 100k/1M harness records which index wins at scale.
        var indexDefinitions = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT indexdef AS "Value" FROM pg_indexes
                WHERE schemaname = 'romd' AND tablename = 'Jobs'
                """)
            .ToListAsync();
        indexDefinitions.ShouldContain(definition =>
            definition.Contains("IX_Jobs_Diagnostics_ReplaceDat", StringComparison.Ordinal)
            && definition.EndsWith("(\"JobType\", \"Phase\", \"UpdatedAt\", \"Id\")", StringComparison.Ordinal));
        indexDefinitions.ShouldContain(definition =>
            definition.Contains("IX_Jobs_Diagnostics_BulkEnrichment", StringComparison.Ordinal)
            && definition.EndsWith(
                "(\"JobType\", \"Phase\", \"HangfireJobId\", \"CreatedAt\", \"Id\")",
                StringComparison.Ordinal));
        replacePlan.ShouldContain(
            plan => plan.Contains("Index Scan", StringComparison.Ordinal)
                    || plan.Contains("Index Only Scan", StringComparison.Ordinal),
            string.Join(Environment.NewLine, replacePlan));
        bulkPlan.ShouldContain(
            plan => plan.Contains("Index Scan", StringComparison.Ordinal)
                    || plan.Contains("Index Only Scan", StringComparison.Ordinal),
            string.Join(Environment.NewLine, bulkPlan));
    }

    [Fact]
    public async Task ReadOutboxAsync_ReturnsFreshnessAndRetryCounts()
    {
        await using var db = CreateDb();
        db.AdminRealtimeOutboxEvents.AddRange(
            Outbox(Now.AddMinutes(-10), processedAt: null, attempts: 2, error: "retrying"),
            Outbox(Now.AddMinutes(-2), processedAt: null),
            Outbox(Now.AddMinutes(-20), processedAt: Now.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var result = await new OperationalDiagnosticsReader(db, _timeProvider).ReadOutboxAsync();

        result.PendingCount.ShouldBe(2);
        result.OldestPendingAt.ShouldBe(Now.AddMinutes(-10));
        result.FailingCount.ShouldBe(1);
        result.LastProcessedAt.ShouldBe(Now.AddMinutes(-1));
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_options);

    // The fixture tables are tiny, so the planner would prefer a sequential scan; disabling it
    // makes the plan show which index the query can use, which is the property under test.
    private static async Task<IReadOnlyList<string>> ExplainAsync(RomdDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN {sql}";
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(0));
        }

        return details;
    }

    private static PlatformEntity Platform(int id, string name, CatalogRebuildState state) => new()
    {
        Id = id,
        Name = name,
        Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = name, BaseCompactLabel = name, CanonicalKey = name.ToLowerInvariant(), ShortName = name.ToLowerInvariant(),
        CatalogRebuildState = state,
        CatalogRebuildError = state == CatalogRebuildState.Failed ? "boom" : null,
        CatalogRebuildFailedAtUtc = state == CatalogRebuildState.Failed ? Now.AddMinutes(-5) : null
    };

    private static ReplaceDatJobEntity ReplaceJob(
        Guid id,
        ReplaceDatPhase phase,
        DateTimeOffset updatedAt,
        DateTimeOffset? startedAt) => new()
    {
        Id = id,
        CorrelationId = Guid.NewGuid(),
        ExistingDatId = 7,
        SourceFilename = "replacement.dat",
        Phase = phase.ToString(),
        ErrorsJson = "[]",
        CreatedAt = Now.AddHours(-3),
        StartedAt = startedAt,
        UpdatedAt = updatedAt,
        JobType = "replace_dat"
    };

    private static BulkEnrichmentJobEntity BulkJob(Guid id, DateTimeOffset createdAt, string? hangfireJobId) => new()
    {
        Id = id,
        CorrelationId = Guid.NewGuid(),
        PlatformId = null,
        SourceFilename = "n64",
        Phase = BulkEnrichmentPhase.Pending.ToString(),
        HangfireJobId = hangfireJobId,
        ErrorsJson = "[]",
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        JobType = "bulk_enrichment"
    };

    private static AdminRealtimeOutboxEventEntity Outbox(
        DateTimeOffset createdAt,
        DateTimeOffset? processedAt,
        int attempts = 0,
        string? error = null) => new()
    {
        EventType = "JobUpdated",
        PayloadJson = "{}",
        CreatedAtUtc = createdAt,
        AvailableAtUtc = createdAt,
        ProcessedAtUtc = processedAt,
        Attempts = attempts,
        LastError = error
    };
}
