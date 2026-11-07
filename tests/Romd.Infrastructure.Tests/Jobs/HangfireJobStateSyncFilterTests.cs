using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Realtime;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;
using Romd.Persistence.Realtime;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class HangfireJobStateSyncFilterTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public HangfireJobStateSyncFilterTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
    }

    [Fact]
    public void OnStateApplied_FailedState_MarksRomdJobFailed()
    {
        var job = UploadJob.Create("library.zip", createdAt: DateTimeOffset.UtcNow);
        job.Start("hangfire-1", startedAt: DateTimeOffset.UtcNow);

        using (var db = CreateDb())
        {
            db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(job));
            db.SaveChanges();
        }

        var failedState = new FailedState(new InvalidOperationException("disk full"));
        var context = CreateApplyStateContext(job.Id, failedState);

        using var provider = CreateServiceProvider();
        var filter = new HangfireJobStateSyncFilter(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ManualTimeProvider(FixedNow),
            NullLogger<HangfireJobStateSyncFilter>.Instance);

        filter.OnStateApplied(context, Substitute.For<IWriteOnlyTransaction>());

        using var assertDb = CreateDb();
        var entity = assertDb.Jobs
            .OfType<UploadJobEntity>()
            .Single(j => j.Id == job.Id);
        var errors = entity.GetErrors();
        var failedAt = new DateTimeOffset(failedState.FailedAt);

        entity.Phase.ShouldBe("Failed");
        entity.CompletedAt.ShouldNotBeNull();
        entity.CompletedAt.Value.ToUnixTimeMilliseconds().ShouldBe(failedAt.ToUnixTimeMilliseconds());
        entity.UpdatedAt.ShouldBe(FixedNow);
        entity.CurrentItem.ShouldBeNull();
        errors.Count.ShouldBe(1);
        errors[0].Item.ShouldBe("job");
        errors[0].Message.ShouldBe("disk full");
        errors[0].OccurredAt.ShouldBe(failedAt);

        var outboxEvent = assertDb.AdminRealtimeOutboxEvents.Single();
        var payload = AdminRealtimePayloadSerializer.Deserialize<Romd.Contracts.Management.Models.Job>(
            outboxEvent.PayloadJson);

        outboxEvent.EventType.ShouldBe(AdminRealtimeEventTypes.JobUpdated);
        outboxEvent.SchemaVersion.ShouldBe(2);
        outboxEvent.CreatedAtUtc.ShouldBe(FixedNow);
        outboxEvent.AvailableAtUtc.ShouldBe(FixedNow);
        payload.Id.ShouldBe(job.Id);
        payload.Phase.ShouldBe("Failed");
    }

    [Theory]
    [InlineData("Artwork", "Artwork")]
    [InlineData("Enrich", "Enrich")]
    [InlineData(null, null)]
    [InlineData("UnknownFutureMode", null)]
    public async Task ArtworkMode_PersistedScope_CanonicalReadNotifierAndExhaustedTransportAgree(string? persistedMode, string? expected)
    {
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 1, null, createdAt: DateTimeOffset.UtcNow);
        job.Start("hangfire-1", startedAt: DateTimeOffset.UtcNow);
        using (var db = CreateDb())
        {
            var row = ArtworkCompletionJobEntity.FromDomain(job);
            row.ScopeJson = persistedMode is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new { Mode = persistedMode });
            db.Add(row);
            await db.SaveChangesAsync();
        }
        using (var db = CreateDb())
        {
            var restored = (await new Romd.Persistence.Repositories.JobRepository(db, TimeProvider.System).GetByIdAsync(job.Id))!;
            ((ArtworkCompletionJob)restored).Mode.ShouldBe(expected);
            var notifier = new OutboxJobNotifier(new AdminRealtimeOutbox(db, new ManualTimeProvider(FixedNow)), db,
                new AdminRealtimeOutboxNotifierCommitGate(), NullLogger<OutboxJobNotifier>.Instance);
            await notifier.NotifyJobUpdatedAsync(restored);
        }
        using var provider = CreateServiceProvider();
        new HangfireJobStateSyncFilter(provider.GetRequiredService<IServiceScopeFactory>(), new ManualTimeProvider(FixedNow), NullLogger<HangfireJobStateSyncFilter>.Instance)
            .OnStateApplied(CreateApplyStateContext(job.Id, new FailedState(new IOException("transport"))), Substitute.For<IWriteOnlyTransaction>());
        using var assert = CreateDb();
        var events = await assert.AdminRealtimeOutboxEvents.ToListAsync();
        events.Count.ShouldBe(2);
        events.ShouldAllBe(x => x.SchemaVersion == 2);
        foreach (var evt in events)
            AdminRealtimePayloadSerializer.Deserialize<Romd.Contracts.Management.Models.Job>(evt.PayloadJson)
                .ShouldBeOfType<Romd.Contracts.Management.Models.ArtworkCompletionOperation>().Mode.ShouldBe(expected);
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<RomdDbContext>(options => options.UseNpgsql(_connection.ConnectionString));
        return services.BuildServiceProvider();
    }

    private static ApplyStateContext CreateApplyStateContext(Guid jobId, IState state)
    {
        var method = typeof(HangfireEntryPoint).GetMethod(nameof(HangfireEntryPoint.Execute))!;
        var job = new Hangfire.Common.Job(
            typeof(HangfireEntryPoint),
            method,
            [jobId]);
        var backgroundJob = new BackgroundJob("hangfire-1", job, DateTime.UtcNow);

        return new ApplyStateContext(
            Substitute.For<JobStorage>(),
            Substitute.For<IStorageConnection>(),
            Substitute.For<IWriteOnlyTransaction>(),
            backgroundJob,
            state,
            ProcessingState.StateName);
    }

    private sealed class HangfireEntryPoint
    {
        public void Execute(Guid jobId)
        {
        }
    }
}
