using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Jobs;
using Romd.Persistence.Realtime;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Realtime;

public sealed class AdminRealtimeOutboxTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);
    private const int PersistedSchemaVersion = 7;
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;
    private readonly ManualTimeProvider _timeProvider = new(Now);

    public static TheoryData<int, string> StructurallyInvalidLibraryUpdatedPayloads => new()
    {
        { AdminRealtimeSchemaVersions.Initial, "{}" },
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":23,\"Name\":\"Legacy\",\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}"
        },
        { AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity, "{}" },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{IdCoder.Encode(23)}\",\"Name\":\"Current\",\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}}"
        }
    };

    public static TheoryData<int, string> SemanticallyInvalidLibraryUpdatedPayloads => new()
    {
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":0,\"Name\":\"Legacy\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}"
        },
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":23,\"Name\":\" \",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}"
        },
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":23,\"Name\":\"Legacy\",\"NeedsMaterialization\":false,\"ItemCount\":-1,\"ConfigurationState\":\"Valid\"}"
        },
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":23,\"Name\":\"Legacy\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\" \"}"
        },
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":23,\"Name\":\"Legacy\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Bogus\"}"
        },
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":23,\"Name\":\"Legacy\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"valid\"}"
        },
        {
            AdminRealtimeSchemaVersions.Initial,
            "{\"LibraryId\":23,\"Name\":\"Legacy\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid, Invalid\"}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            "{\"LibraryId\":\"invalid!\",\"Name\":\"Current\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{FindNonCanonicalSqid()}\",\"Name\":\"Current\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{IdCoder.Encode(23)}\",\"Name\":\" \",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{IdCoder.Encode(23)}\",\"Name\":\"Current\",\"NeedsMaterialization\":false,\"ItemCount\":-1,\"ConfigurationState\":\"Valid\"}}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{IdCoder.Encode(23)}\",\"Name\":\"Current\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\" \"}}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{IdCoder.Encode(23)}\",\"Name\":\"Current\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Bogus\"}}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{IdCoder.Encode(23)}\",\"Name\":\"Current\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"valid\"}}"
        },
        {
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            $"{{\"LibraryId\":\"{IdCoder.Encode(23)}\",\"Name\":\"Current\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid, Invalid\"}}"
        }
    };

    public AdminRealtimeOutboxTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
    }

    [Fact]
    public async Task EnqueueAsync_Event_EnlistsPendingRowWithoutSavingOrCommitting()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);

        await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, CancellationToken.None);

        db.Entry(db.AdminRealtimeOutboxEvents.Local.Single()).State.ShouldBe(EntityState.Added);
        await using (var beforeSaveDb = CreateDb())
        {
            (await beforeSaveDb.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        }

        await db.SaveChangesAsync();
        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.EventType.ShouldBe(AdminRealtimeEventTypes.StorageStatsChanged);
        row.PayloadJson.ShouldBe("{}");
        row.SchemaVersion.ShouldBe(AdminRealtimeSchemaVersions.Initial);
        row.CreatedAtUtc.ShouldBe(Now);
        row.AvailableAtUtc.ShouldBe(Now);
        row.ProcessedAtUtc.ShouldBeNull();
        row.Attempts.ShouldBe(0);
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task EnqueueAsync_LibraryUpdatedPayload_UsesOpaqueIdentitySchemaVersion()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var payload = new AdminRealtimeLibraryUpdatedPayload(
            IdCoder.Encode(17),
            "Curated",
            true,
            9,
            "Valid");

        await outbox.EnqueueAsync(AdminRealtimeEventTypes.LibraryUpdated, payload, CancellationToken.None);
        await db.SaveChangesAsync();

        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.SchemaVersion.ShouldBe(AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity);
        AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeLibraryUpdatedPayload>(row.PayloadJson)
            .ShouldBe(payload);
    }

    [Fact]
    public async Task SchemaVersion_RowInsertedWithoutIt_DefaultsToInitialAndRoundTripsThroughClaim()
    {
        await using var db = CreateDb();
        await db.Database.ExecuteSqlRawAsync(
            $$"""
             INSERT INTO {{PostgreSqlConfiguration.SchemaName}}."AdminRealtimeOutboxEvents"
                 ("EventType", "PayloadJson", "CreatedAtUtc", "AvailableAtUtc", "Attempts")
             VALUES
                 ({0}, {1}, {2}, {3}, 0)
             """,
            AdminRealtimeEventTypes.HealthStatsChanged,
            "{}",
            Now.ToUnixTimeMilliseconds(),
            Now.ToUnixTimeMilliseconds());

        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.SchemaVersion.ShouldBe(AdminRealtimeSchemaVersions.Initial);

        string? columnDefault = await db.Database
            .SqlQueryRaw<string?>(
                $"""
                 SELECT column_default AS "Value"
                 FROM information_schema.columns
                 WHERE table_schema = '{PostgreSqlConfiguration.SchemaName}'
                   AND table_name = 'AdminRealtimeOutboxEvents'
                   AND column_name = 'SchemaVersion'
                 """)
            .SingleAsync();
        columnDefault.ShouldBe("1");

        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var claimed = (await outbox.ClaimPendingAsync(1, TimeSpan.FromMinutes(1))).Single();
        claimed.SchemaVersion.ShouldBe(AdminRealtimeSchemaVersions.Initial);
    }

    [Fact]
    public async Task ClaimPendingAsync_PendingRows_ReturnsOrderedRowsAndLeasesThem()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.CoverageStatsChanged);
        await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.HealthStatsChanged);

        var messages = await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None);

        messages.Select(message => message.EventType).ShouldBe([
            AdminRealtimeEventTypes.CoverageStatsChanged,
            AdminRealtimeEventTypes.HealthStatsChanged
        ]);

        var rows = await db.AdminRealtimeOutboxEvents
            .OrderBy(row => row.Id)
            .ToListAsync();
        rows.ShouldAllBe(row => row.Attempts == 1);
        rows.ShouldAllBe(row => row.ClaimId != null);
        rows.ShouldAllBe(row => row.ClaimedAtUtc == Now);
        rows.ShouldAllBe(row => row.AvailableAtUtc == Now.AddMinutes(1));
    }

    [Fact]
    public async Task ClaimPendingAsync_UnexpiredLease_DoesNotReturnDuplicate()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.HealthStatsChanged);

        var firstClaim = await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None);
        var secondClaim = await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None);

        firstClaim.Count.ShouldBe(1);
        secondClaim.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimPendingAsync_DispatcherShutsDownAfterClaim_ReclaimsRowAfterLeaseExpiry()
    {
        int claimedId;
        await using (var firstProcessDb = CreateDb())
        {
            var firstProcessOutbox = new AdminRealtimeOutbox(firstProcessDb, _timeProvider);
            await EnqueueAndSaveAsync(firstProcessOutbox, firstProcessDb, AdminRealtimeEventTypes.HealthStatsChanged);
            claimedId = (await firstProcessOutbox.ClaimPendingAsync(
                10,
                TimeSpan.FromMinutes(1),
                CancellationToken.None)).Single().Id;
        }

        _timeProvider.Advance(TimeSpan.FromMinutes(2));

        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var messages = await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None);

        messages.Count.ShouldBe(1);
        messages.Single().Id.ShouldBe(claimedId);
        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.Attempts.ShouldBe(2);
        row.ClaimedAtUtc.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public async Task MarkProcessedAsync_ClaimedRow_SetsProcessedAtAndClearsClaim()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.HealthStatsChanged);
        var message = (await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None)).Single();

        await outbox.MarkProcessedAsync(message.Id, CancellationToken.None);

        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.ProcessedAtUtc.ShouldBe(Now);
        row.ClaimId.ShouldBeNull();
        row.ClaimedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_ClaimedRow_StoresErrorAndSchedulesRetry()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.HealthStatsChanged);
        var message = (await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None)).Single();

        await outbox.MarkFailedAsync(message.Id, "hub disconnected", TimeSpan.FromSeconds(30), CancellationToken.None);

        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.ProcessedAtUtc.ShouldBeNull();
        row.LastError.ShouldBe("hub disconnected");
        row.AvailableAtUtc.ShouldBe(Now.AddSeconds(30));
        row.ClaimId.ShouldBeNull();
        row.ClaimedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteProcessedOlderThanAsync_ProcessedRows_RemovesOnlyExpiredRows()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.StorageStatsChanged);
        await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.CoverageStatsChanged);
        var oldMessage = (await outbox.ClaimPendingAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None)).Single();
        await outbox.MarkProcessedAsync(oldMessage.Id, CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromDays(8));

        int deleted = await outbox.DeleteProcessedOlderThanAsync(TimeSpan.FromDays(7), CancellationToken.None);

        deleted.ShouldBe(1);
        var remaining = await db.AdminRealtimeOutboxEvents.SingleAsync();
        remaining.EventType.ShouldBe(AdminRealtimeEventTypes.CoverageStatsChanged);
        remaining.ProcessedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task OutboxJobNotifier_NotifyJobUpdatedAsync_EnqueuesContractPayload()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var notifier = new OutboxJobNotifier(
            outbox,
            db,
            new AdminRealtimeOutboxNotifierCommitGate(),
            NullLogger<OutboxJobNotifier>.Instance);
        db.Platforms.Add(new PlatformEntity { Id = 42, Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "local-test", ShortName = "local-test", Name = "Test" });
        await db.SaveChangesAsync();
        var job = UploadJob.Create("library.zip", platformId: 42);
        job.Start("hangfire-1");

        await notifier.NotifyJobUpdatedAsync(job, CancellationToken.None);

        var message = (await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None)).Single();
        var payload = AdminRealtimePayloadSerializer.Deserialize<JobDto>(message.PayloadJson);
        var upload = payload.ShouldBeOfType<UploadJobDto>();
        upload.Id.ShouldBe(job.Id);
        upload.SystemKey.ShouldNotBeNull();
        upload.Phase.ShouldBe(job.Phase);
    }

    [Fact]
    public async Task OutboxJobNotifier_NotifyTitleEnrichedAsync_EnqueuesInternalIdPayload()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var notifier = new OutboxJobNotifier(
            outbox,
            db,
            new AdminRealtimeOutboxNotifierCommitGate(),
            NullLogger<OutboxJobNotifier>.Instance);

        await notifier.NotifyTitleEnrichedAsync(10, 20, 30, CancellationToken.None);

        var message = (await outbox.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None)).Single();
        var payload = AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeTitleEnrichedOutboxPayload>(message.PayloadJson);
        message.EventType.ShouldBe(AdminRealtimeEventTypes.TitleEnriched);
        payload.ShouldBe(new AdminRealtimeTitleEnrichedOutboxPayload(10, 20, 30));
    }

    [Fact]
    public async Task OutboxStatsNotifier_NotifyHealthChangedAsync_EnqueuesHealthStatsEvent()
    {
        await using var db = CreateDb();
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var notifier = new OutboxStatsNotifier(
            outbox,
            db,
            new AdminRealtimeOutboxNotifierCommitGate(),
            NullLogger<OutboxStatsNotifier>.Instance);

        await notifier.NotifyHealthChangedAsync(CancellationToken.None);

        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.EventType.ShouldBe(AdminRealtimeEventTypes.HealthStatsChanged);
        row.PayloadJson.ShouldBe("{}");
    }

    [Fact]
    public async Task OutboxStatsNotifier_ConcurrentNotifications_SerializesScopedContextSaves()
    {
        var interceptor = new DelayingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new RomdDbContext(options);
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var notifier = new OutboxStatsNotifier(
            outbox,
            db,
            new AdminRealtimeOutboxNotifierCommitGate(),
            NullLogger<OutboxStatsNotifier>.Instance);

        var first = notifier.NotifyStorageChangedAsync(CancellationToken.None);
        await interceptor.FirstSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = notifier.NotifyCoverageChangedAsync(CancellationToken.None);
        interceptor.ReleaseFirstSave.TrySetResult(true);

        await Task.WhenAll(first, second);

        interceptor.SaveCalls.ShouldBe(2);
        interceptor.MaximumConcurrentSaves.ShouldBe(1);
        var eventTypes = await db.AdminRealtimeOutboxEvents
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.EventType)
            .ToListAsync();
        eventTypes.ShouldBe([
            AdminRealtimeEventTypes.StorageStatsChanged,
            AdminRealtimeEventTypes.CoverageStatsChanged
        ]);
    }

    [Fact]
    public async Task ScopedOutboxNotifiers_ConcurrentNotifications_SerializeContextSaves()
    {
        var interceptor = new DelayingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new RomdDbContext(options);
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var commitGate = new AdminRealtimeOutboxNotifierCommitGate();
        var statsNotifier = new OutboxStatsNotifier(
            outbox,
            db,
            commitGate,
            NullLogger<OutboxStatsNotifier>.Instance);
        var jobNotifier = new OutboxJobNotifier(
            outbox,
            db,
            commitGate,
            NullLogger<OutboxJobNotifier>.Instance);

        var statsNotification = statsNotifier.NotifyStorageChangedAsync(CancellationToken.None);
        await interceptor.FirstSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var jobNotification = jobNotifier.NotifyJobUpdatedAsync(
            UploadJob.Create("library.zip"),
            CancellationToken.None);
        interceptor.ReleaseFirstSave.TrySetResult(true);

        await Task.WhenAll(statsNotification, jobNotification);

        interceptor.SaveCalls.ShouldBe(2);
        interceptor.MaximumConcurrentSaves.ShouldBe(1);
        var eventTypes = await db.AdminRealtimeOutboxEvents
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.EventType)
            .ToListAsync();
        eventTypes.ShouldBe([
            AdminRealtimeEventTypes.StorageStatsChanged,
            AdminRealtimeEventTypes.JobUpdated
        ]);
    }

    [Fact]
    public async Task OutboxStatsNotifier_SaveFails_DetachesOnlyItsUncommittedEvent()
    {
        var interceptor = new FailNextSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new RomdDbContext(options);
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var notifier = new OutboxStatsNotifier(
            outbox,
            db,
            new AdminRealtimeOutboxNotifierCommitGate(),
            NullLogger<OutboxStatsNotifier>.Instance);
        db.AdminRealtimeOutboxEvents.Add(CreateTrackedEvent(AdminRealtimeEventTypes.CoverageStatsChanged));

        await notifier.NotifyHealthChangedAsync(CancellationToken.None);

        interceptor.FailNextSave = false;
        await db.SaveChangesAsync();
        var persisted = await db.AdminRealtimeOutboxEvents.SingleAsync();
        persisted.EventType.ShouldBe(AdminRealtimeEventTypes.CoverageStatsChanged);
    }

    [Fact]
    public async Task OutboxJobNotifier_SaveFails_DetachesOnlyItsUncommittedEvent()
    {
        var interceptor = new FailNextSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new RomdDbContext(options);
        var outbox = new AdminRealtimeOutbox(db, _timeProvider);
        var notifier = new OutboxJobNotifier(
            outbox,
            db,
            new AdminRealtimeOutboxNotifierCommitGate(),
            NullLogger<OutboxJobNotifier>.Instance);
        db.AdminRealtimeOutboxEvents.Add(CreateTrackedEvent(AdminRealtimeEventTypes.StorageStatsChanged));

        await notifier.NotifyJobUpdatedAsync(UploadJob.Create("library.zip"), CancellationToken.None);

        interceptor.FailNextSave = false;
        await db.SaveChangesAsync();
        var persisted = await db.AdminRealtimeOutboxEvents.SingleAsync();
        persisted.EventType.ShouldBe(AdminRealtimeEventTypes.StorageStatsChanged);
    }

    [Fact]
    public async Task ScopedOutboxPorts_ResolveSameConcreteInstance()
    {
        await using var provider = await CreateServiceProvider(new RecordingAdminRealtimeSink());
        await using var scope = provider.CreateAsyncScope();

        var enqueuePort = scope.ServiceProvider.GetRequiredService<IAdminEventOutbox>();
        var dispatchPort = scope.ServiceProvider.GetRequiredService<IAdminRealtimeOutbox>();

        ReferenceEquals(enqueuePort, dispatchPort).ShouldBeTrue();
    }

    [Fact]
    public async Task AdminRealtimeOutboxDispatcher_DispatchPendingAsync_ForwardsEventsAndMarksProcessed()
    {
        var sink = new RecordingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink);
        await EnqueueAllEventTypesAsync(provider);
        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        int dispatched = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        dispatched.ShouldBe(6);
        sink.JobUpdates.Count.ShouldBe(1);
        sink.TitleEnrichedPayloads.ShouldBe([new AdminRealtimeTitleEnrichedOutboxPayload(1, 2, 3)]);
        sink.LibraryUpdatedPayloads.ShouldBe([
            new AdminRealtimeLibraryUpdatedPayload(IdCoder.Encode(4), "Arcade", false, 12, "Valid")
        ]);
        sink.StorageChangedCount.ShouldBe(1);
        sink.CoverageChangedCount.ShouldBe(1);
        sink.HealthChangedCount.ShouldBe(1);
        sink.SchemaVersions.ShouldBe([
            PersistedSchemaVersion,
            PersistedSchemaVersion,
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            PersistedSchemaVersion,
            PersistedSchemaVersion,
            PersistedSchemaVersion
        ]);

        await using var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var processed = await db.AdminRealtimeOutboxEvents
            .Where(row => row.ProcessedAtUtc != null)
            .CountAsync();
        processed.ShouldBe(6);
    }

    [Fact]
    public async Task AdminRealtimeOutboxDispatcher_DispatchPendingAsync_WhenSinkFailsSchedulesRetry()
    {
        var sink = new ThrowingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IAdminEventOutbox>();
            var enqueueDb = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await EnqueueAndSaveAsync(outbox, enqueueDb, AdminRealtimeEventTypes.HealthStatsChanged);
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        int dispatched = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        dispatched.ShouldBe(1);
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var row = await db.AdminRealtimeOutboxEvents.SingleAsync();
        row.ProcessedAtUtc.ShouldBeNull();
        row.LastError.ShouldNotBeNull();
        row.LastError.ShouldContain("SignalR unavailable");
        row.AvailableAtUtc.ShouldBe(Now.AddSeconds(30));
    }

    [Fact]
    public async Task AdminRealtimeOutboxDispatcher_UnsupportedLibraryUpdatedVersion_RetainsRowForRetry()
    {
        var sink = new RecordingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.Add(CreatePersistedEvent(
                AdminRealtimeEventTypes.LibraryUpdated,
                AdminRealtimePayloadSerializer.Serialize(new AdminRealtimeLibraryUpdatedPayload(
                    IdCoder.Encode(19),
                    "Future",
                    false,
                    1,
                    "Valid")),
                schemaVersion: 3));
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(1);

        sink.LibraryUpdatedPayloads.ShouldBeEmpty();
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var row = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .SingleAsync();
        row.ProcessedAtUtc.ShouldBeNull();
        row.LastError.ShouldNotBeNull();
        row.LastError.ShouldContain("Unsupported LibraryUpdated schema version '3'");
        row.AvailableAtUtc.ShouldBe(Now.AddSeconds(30));
    }

    [Fact]
    public async Task AdminRealtimeOutboxDispatcher_MalformedLibraryUpdatedV1_RetainsRowForRetry()
    {
        var sink = new RecordingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.Add(CreatePersistedEvent(
                AdminRealtimeEventTypes.LibraryUpdated,
                "{\"LibraryId\":\"not-an-integer\"}",
                AdminRealtimeSchemaVersions.Initial));
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(1);

        sink.LibraryUpdatedPayloads.ShouldBeEmpty();
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var row = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .SingleAsync();
        row.ProcessedAtUtc.ShouldBeNull();
        row.LastError.ShouldNotBeNullOrWhiteSpace();
        row.AvailableAtUtc.ShouldBe(Now.AddSeconds(30));
    }

    [Theory]
    [MemberData(nameof(StructurallyInvalidLibraryUpdatedPayloads))]
    [MemberData(nameof(SemanticallyInvalidLibraryUpdatedPayloads))]
    public async Task AdminRealtimeOutboxDispatcher_InvalidLibraryUpdatedPayload_RetainsRowWithoutSending(
        int schemaVersion,
        string payloadJson)
    {
        var sink = new RecordingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.Add(CreatePersistedEvent(
                AdminRealtimeEventTypes.LibraryUpdated,
                payloadJson,
                schemaVersion));
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(1);

        sink.LibraryUpdatedPayloads.ShouldBeEmpty();
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var row = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .SingleAsync();
        row.ProcessedAtUtc.ShouldBeNull();
        row.LastError.ShouldNotBeNullOrWhiteSpace();
        row.AvailableAtUtc.ShouldBe(Now.AddSeconds(30));
    }

    [Theory]
    [InlineData(AdminRealtimeSchemaVersions.Initial, "Invalid")]
    [InlineData(AdminRealtimeSchemaVersions.Initial, "RequiresMigration")]
    [InlineData(AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity, "Invalid")]
    [InlineData(AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity, "RequiresMigration")]
    public async Task AdminRealtimeOutboxDispatcher_KnownLibraryConfigurationState_Delivers(
        int schemaVersion,
        string configurationState)
    {
        string libraryId = schemaVersion == AdminRealtimeSchemaVersions.Initial
            ? "23"
            : $"\"{IdCoder.Encode(23)}\"";
        string payloadJson =
            $"{{\"LibraryId\":{libraryId},\"Name\":\"Library\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"{configurationState}\"}}";
        var sink = new RecordingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.Add(CreatePersistedEvent(
                AdminRealtimeEventTypes.LibraryUpdated,
                payloadJson,
                schemaVersion));
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(1);

        sink.LibraryUpdatedPayloads.ShouldHaveSingleItem().ConfigurationState.ShouldBe(configurationState);
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var row = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .SingleAsync();
        row.ProcessedAtUtc.ShouldBe(Now);
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task AdminRealtimeOutboxDispatcher_SinkFailsOnce_RetriesThroughSuccessAndMarksProcessed()
    {
        var sink = new FailOnceAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IAdminEventOutbox>();
            var enqueueDb = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await EnqueueAndSaveAsync(outbox, enqueueDb, AdminRealtimeEventTypes.HealthStatsChanged);
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(1);
        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(1);

        sink.HealthChangedCount.ShouldBe(1);
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var rows = await db.AdminRealtimeOutboxEvents.ToListAsync();
        rows.Count.ShouldBe(1);
        rows.Single().Attempts.ShouldBe(2);
        rows.Single().ProcessedAtUtc.ShouldBe(Now.AddSeconds(31));
        rows.Single().LastError.ShouldBeNull();
    }

    [Fact]
    public async Task AdminRealtimeOutboxDispatcher_ProcessedAcknowledgementFails_RedeliversAtLeastOnceAfterLeaseExpiry()
    {
        var sink = new RecordingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink, failFirstProcessedAcknowledgement: true);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IAdminEventOutbox>();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await EnqueueAndSaveAsync(outbox, db, AdminRealtimeEventTypes.HealthStatsChanged);
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        await dispatcher.DispatchPendingAsync(CancellationToken.None);
        sink.HealthChangedCount.ShouldBe(1);

        // The sink succeeded, but durable acknowledgement failed. At-least-once
        // delivery intentionally allows this event to be observed again.
        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        sink.HealthChangedCount.ShouldBe(2);
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var row = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .SingleAsync();
        row.Attempts.ShouldBe(2);
        row.ProcessedAtUtc.ShouldBe(Now.AddSeconds(31));
    }

    [Fact]
    public async Task AdminRealtimeOutboxDispatcher_LegacyTranslationAcknowledgementFails_RedeliversSameOpaquePayload()
    {
        var sink = new RecordingAdminRealtimeSink();
        await using var provider = await CreateServiceProvider(sink, failFirstProcessedAcknowledgement: true);
        await using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.Add(CreatePersistedEvent(
                AdminRealtimeEventTypes.LibraryUpdated,
                "{\"LibraryId\":23,\"Name\":\"Legacy\",\"NeedsMaterialization\":false,\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}",
                AdminRealtimeSchemaVersions.Initial));
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        await dispatcher.DispatchPendingAsync(CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromSeconds(31));
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        sink.LibraryUpdatedPayloads.ShouldBe([
            new AdminRealtimeLibraryUpdatedPayload(IdCoder.Encode(23), "Legacy", false, 4, "Valid"),
            new AdminRealtimeLibraryUpdatedPayload(IdCoder.Encode(23), "Legacy", false, 4, "Valid")
        ]);
        sink.SchemaVersions.ShouldBe([
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity
        ]);
        await using var assertScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var row = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .SingleAsync();
        row.Attempts.ShouldBe(2);
        row.ProcessedAtUtc.ShouldBe(Now.AddSeconds(31));
    }

    public void Dispose() => _connection.Dispose();

    private static string FindNonCanonicalSqid()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        foreach (char first in alphabet)
        {
            string candidate = first.ToString();
            if (IsNonCanonical(candidate))
            {
                return candidate;
            }

            foreach (char second in alphabet)
            {
                candidate = string.Concat(first, second);
                if (IsNonCanonical(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("Could not construct a noncanonical Sqid fixture.");
    }

    private static bool IsNonCanonical(string candidate) =>
        IdCoder.TryDecode(candidate, out int decoded)
        && !string.Equals(IdCoder.Encode(decoded), candidate, StringComparison.Ordinal);

    private RomdDbContext CreateDb() => new(_contextOptions);

    private async Task<ServiceProvider> CreateServiceProvider(
        IAdminRealtimeEventSink sink,
        bool failFirstProcessedAcknowledgement = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddDbContext<RomdDbContext>(options => options.UseNpgsql(_connection.ConnectionString));
        services.AddScoped<AdminRealtimeOutbox>();
        services.AddScoped<IAdminEventOutbox>(provider => provider.GetRequiredService<AdminRealtimeOutbox>());
        if (failFirstProcessedAcknowledgement)
        {
            services.AddSingleton<FailedProcessedAcknowledgement>();
            services.AddScoped<IAdminRealtimeOutbox>(provider => new FailFirstProcessedAcknowledgementOutbox(
                provider.GetRequiredService<AdminRealtimeOutbox>(),
                provider.GetRequiredService<FailedProcessedAcknowledgement>()));
        }
        else
        {
            services.AddScoped<IAdminRealtimeOutbox>(provider => provider.GetRequiredService<AdminRealtimeOutbox>());
        }

        services.AddScoped(_ => sink);

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Database.EnsureCreatedAsync();
        return provider;
    }

    private static async Task EnqueueAllEventTypesAsync(ServiceProvider provider)
    {
        await using var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var job = UploadJob.Create("library.zip");
        JobDto jobDto = Romd.Admin.Application.Ingestion.Jobs.JobMapping.ToContract((Job)job, new Romd.Application.Common.Systems.SystemKeys(new Dictionary<int, string>()));

        db.AdminRealtimeOutboxEvents.AddRange(
            CreatePersistedEvent(AdminRealtimeEventTypes.JobUpdated, AdminRealtimePayloadSerializer.Serialize(jobDto)),
            CreatePersistedEvent(
                AdminRealtimeEventTypes.TitleEnriched,
                AdminRealtimePayloadSerializer.Serialize(new AdminRealtimeTitleEnrichedOutboxPayload(1, 2, 3))),
            CreatePersistedEvent(
                AdminRealtimeEventTypes.LibraryUpdated,
                AdminRealtimePayloadSerializer.Serialize(
                    new AdminRealtimeLibraryUpdatedPayload(IdCoder.Encode(4), "Arcade", false, 12, "Valid")),
                AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity),
            CreatePersistedEvent(AdminRealtimeEventTypes.StorageStatsChanged, "{}"),
            CreatePersistedEvent(AdminRealtimeEventTypes.CoverageStatsChanged, "{}"),
            CreatePersistedEvent(AdminRealtimeEventTypes.HealthStatsChanged, "{}"));
        await db.SaveChangesAsync();
    }

    private static AdminRealtimeOutboxEventEntity CreatePersistedEvent(
        string eventType,
        string payloadJson,
        int schemaVersion = PersistedSchemaVersion) =>
        new()
        {
            EventType = eventType,
            PayloadJson = payloadJson,
            SchemaVersion = schemaVersion,
            CreatedAtUtc = Now,
            AvailableAtUtc = Now
        };

    private static AdminRealtimeOutboxEventEntity CreateTrackedEvent(string eventType) =>
        new()
        {
            EventType = eventType,
            PayloadJson = "{}",
            SchemaVersion = AdminRealtimeSchemaVersions.Initial,
            CreatedAtUtc = Now,
            AvailableAtUtc = Now
        };

    private static async Task EnqueueAndSaveAsync(
        IAdminEventOutbox outbox,
        RomdDbContext db,
        string eventType)
    {
        await outbox.EnqueueAsync(eventType, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private class RecordingAdminRealtimeSink : IAdminRealtimeEventSink
    {
        public List<JobDto> JobUpdates { get; } = [];
        public List<AdminRealtimeTitleEnrichedOutboxPayload> TitleEnrichedPayloads { get; } = [];
        public List<AdminRealtimeLibraryUpdatedPayload> LibraryUpdatedPayloads { get; } = [];
        public List<int> SchemaVersions { get; } = [];
        public int StorageChangedCount { get; private set; }
        public int CoverageChangedCount { get; private set; }
        public int HealthChangedCount { get; private set; }

        public virtual Task SendJobUpdatedAsync(JobDto job, int schemaVersion, CancellationToken ct = default)
        {
            JobUpdates.Add(job);
            SchemaVersions.Add(schemaVersion);
            return Task.CompletedTask;
        }

        public virtual Task SendTitleEnrichedAsync(
            AdminRealtimeTitleEnrichedOutboxPayload payload,
            int schemaVersion,
            CancellationToken ct = default)
        {
            TitleEnrichedPayloads.Add(payload);
            SchemaVersions.Add(schemaVersion);
            return Task.CompletedTask;
        }

        public virtual Task SendLibraryUpdatedAsync(
            AdminRealtimeLibraryUpdatedPayload payload,
            int schemaVersion,
            CancellationToken ct = default)
        {
            LibraryUpdatedPayloads.Add(payload);
            SchemaVersions.Add(schemaVersion);
            return Task.CompletedTask;
        }

        public virtual Task SendStorageChangedAsync(int schemaVersion, CancellationToken ct = default)
        {
            StorageChangedCount++;
            SchemaVersions.Add(schemaVersion);
            return Task.CompletedTask;
        }

        public virtual Task SendCoverageChangedAsync(int schemaVersion, CancellationToken ct = default)
        {
            CoverageChangedCount++;
            SchemaVersions.Add(schemaVersion);
            return Task.CompletedTask;
        }

        public virtual Task SendHealthChangedAsync(int schemaVersion, CancellationToken ct = default)
        {
            HealthChangedCount++;
            SchemaVersions.Add(schemaVersion);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAdminRealtimeSink : RecordingAdminRealtimeSink
    {
        public override Task SendHealthChangedAsync(int schemaVersion, CancellationToken ct = default)
            => throw new InvalidOperationException("SignalR unavailable");
    }

    private sealed class FailOnceAdminRealtimeSink : RecordingAdminRealtimeSink
    {
        private bool _shouldFail = true;

        public override Task SendHealthChangedAsync(int schemaVersion, CancellationToken ct = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                throw new InvalidOperationException("SignalR temporarily unavailable");
            }

            return base.SendHealthChangedAsync(schemaVersion, ct);
        }
    }

    private sealed class FailedProcessedAcknowledgement
    {
        public bool ShouldFail { get; set; } = true;
    }

    private sealed class FailFirstProcessedAcknowledgementOutbox(
        IAdminRealtimeOutbox inner,
        FailedProcessedAcknowledgement failure) : IAdminRealtimeOutbox
    {
        public Task<IReadOnlyList<AdminRealtimeOutboxMessage>> ClaimPendingAsync(
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken ct = default) =>
            inner.ClaimPendingAsync(batchSize, leaseDuration, ct);

        public Task MarkProcessedAsync(int id, CancellationToken ct = default)
        {
            if (failure.ShouldFail)
            {
                failure.ShouldFail = false;
                throw new InvalidOperationException("processed acknowledgement unavailable");
            }

            return inner.MarkProcessedAsync(id, ct);
        }

        public Task MarkFailedAsync(
            int id,
            string error,
            TimeSpan retryDelay,
            CancellationToken ct = default) =>
            inner.MarkFailedAsync(id, error, retryDelay, ct);

        public Task<int> DeleteProcessedOlderThanAsync(TimeSpan olderThan, CancellationToken ct = default) =>
            inner.DeleteProcessedOlderThanAsync(olderThan, ct);
    }

    private sealed class FailNextSaveChangesInterceptor : SaveChangesInterceptor
    {
        public bool FailNextSave { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                throw new DbUpdateException("Injected outbox save failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class DelayingSaveChangesInterceptor : SaveChangesInterceptor
    {
        private int _activeSaves;

        public TaskCompletionSource<bool> FirstSaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveCalls { get; private set; }
        public int MaximumConcurrentSaves { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            int activeSaves = Interlocked.Increment(ref _activeSaves);
            MaximumConcurrentSaves = Math.Max(MaximumConcurrentSaves, activeSaves);
            int saveCall = ++SaveCalls;

            try
            {
                if (saveCall == 1)
                {
                    FirstSaveEntered.TrySetResult(true);
                    await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
                }

                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _activeSaves);
            }
        }
    }
}
