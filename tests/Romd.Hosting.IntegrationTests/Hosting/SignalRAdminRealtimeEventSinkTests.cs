using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Romd.Hosting.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Realtime;
using Romd.Host.Hubs;
using Romd.Hosting.Realtime;
using Romd.Persistence.Realtime;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Realtime;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class SignalRAdminRealtimeEventSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 4, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dispatcher_WhenSignalRSendFails_RetainsOutboxRowForRetry()
    {
        await using var connection = PostgreSqlTestDatabase.Create();
        await using var provider = CreateServiceProvider(
            connection,
            CreateJobHubContext(),
            CreateFailingSystemHubContext());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IAdminEventOutbox>();
            await outbox.EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        int dispatched = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        dispatched.ShouldBe(1);
        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var row = await assertDb.AdminRealtimeOutboxEvents.SingleAsync();
        row.ProcessedAtUtc.ShouldBeNull();
        row.LastError.ShouldNotBeNull();
        row.LastError.ShouldContain("SignalR unavailable");
        row.AvailableAtUtc.ShouldBe(Now.AddSeconds(30));
    }

    [Fact]
    public async Task Dispatcher_PersistedSchemaVersion_DeliversAsTrailingSignalRArgument()
    {
        const int schemaVersion = 7;
        await using var connection = PostgreSqlTestDatabase.Create();
        var jobHubContext = CreateJobHubContext(out var jobProxy);
        var systemHubContext = CreateSystemHubContext(out var systemProxy);
        await using var provider = CreateServiceProvider(connection, jobHubContext, systemHubContext);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.AddRange(
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.TitleEnriched,
                    PayloadJson = AdminRealtimePayloadSerializer.Serialize(
                        new AdminRealtimeTitleEnrichedOutboxPayload(1, 2, 3)),
                    SchemaVersion = schemaVersion,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                },
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.HealthStatsChanged,
                    PayloadJson = "{}",
                    SchemaVersion = schemaVersion,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                });
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(2);

        await jobProxy.Received(1).SendCoreAsync(
            "TitleEnriched",
            Arg.Is<object?[]>(arguments =>
                arguments.Length == 2 && Equals(arguments[arguments.Length - 1], schemaVersion)),
            Arg.Any<CancellationToken>());
        await systemProxy.Received(1).SendCoreAsync(
            "HealthStatsChanged",
            Arg.Is<object?[]>(arguments =>
                arguments.Length == 1 && Equals(arguments[arguments.Length - 1], schemaVersion)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_LibraryUpdatedV1AndV2Rows_DeliverOpaqueV2PayloadsAndMarkProcessed()
    {
        const int legacyLibraryId = 41;
        const int currentLibraryId = 43;
        await using var connection = PostgreSqlTestDatabase.Create();
        var systemHubContext = CreateSystemHubContext(out var systemProxy);
        await using var provider = CreateServiceProvider(
            connection,
            CreateJobHubContext(),
            systemHubContext);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.AddRange(
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.LibraryUpdated,
                    PayloadJson =
                        $$"""{"LibraryId":{{legacyLibraryId}},"Name":"Legacy","NeedsMaterialization":true,"ItemCount":2,"ConfigurationState":"Valid"}""",
                    SchemaVersion = 1,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                },
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.LibraryUpdated,
                    PayloadJson =
                        $$"""{"LibraryId":"{{IdCoder.Encode(currentLibraryId)}}","Name":"Current","NeedsMaterialization":false,"ItemCount":5,"ConfigurationState":"Valid"}""",
                    SchemaVersion = 2,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                });
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(2);

        var deliveries = systemProxy.ReceivedCalls()
            .Where(call => Equals(call.GetArguments()[0], AdminRealtimeEventTypes.LibraryUpdated))
            .Select(call => (object?[])call.GetArguments()[1]!)
            .ToList();
        deliveries.Count.ShouldBe(2);
        deliveries.ShouldAllBe(arguments =>
            arguments.Length == 2 && Equals(arguments[1], 2));
        deliveries
            .Select(arguments => arguments[0].ShouldBeOfType<AdminRealtimeLibraryUpdatedPayload>().LibraryId)
            .ShouldBe([IdCoder.Encode(legacyLibraryId), IdCoder.Encode(currentLibraryId)]);

        await using var assertScope = provider.CreateAsyncScope();
        var rows = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .ToListAsync();
        rows.ShouldAllBe(row => row.ProcessedAtUtc == Now);
    }

    [Fact]
    public async Task Dispatcher_InvalidLibraryUpdatedV1AndV2Rows_RetainsWithoutSignalRDelivery()
    {
        await using var connection = PostgreSqlTestDatabase.Create();
        var systemHubContext = CreateSystemHubContext(out var systemProxy);
        await using var provider = CreateServiceProvider(
            connection,
            CreateJobHubContext(),
            systemHubContext);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            db.AdminRealtimeOutboxEvents.AddRange(
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.LibraryUpdated,
                    PayloadJson = "{}",
                    SchemaVersion = AdminRealtimeSchemaVersions.Initial,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                },
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.LibraryUpdated,
                    PayloadJson = "{\"LibraryId\":23,\"Name\":\"Legacy\",\"ItemCount\":4,\"ConfigurationState\":\"Valid\"}",
                    SchemaVersion = AdminRealtimeSchemaVersions.Initial,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                },
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.LibraryUpdated,
                    PayloadJson = "{}",
                    SchemaVersion = AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                },
                new AdminRealtimeOutboxEventEntity
                {
                    EventType = AdminRealtimeEventTypes.LibraryUpdated,
                    PayloadJson = $$"""{"LibraryId":"{{IdCoder.Encode(23)}}","Name":"Current","ItemCount":4,"ConfigurationState":"Valid"}""",
                    SchemaVersion = AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
                    CreatedAtUtc = Now,
                    AvailableAtUtc = Now
                });
            await db.SaveChangesAsync();
        }

        var dispatcher = new AdminRealtimeOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdminRealtimeOutboxDispatcher>.Instance);

        (await dispatcher.DispatchPendingAsync(CancellationToken.None)).ShouldBe(4);

        await systemProxy.DidNotReceive().SendCoreAsync(
            AdminRealtimeEventTypes.LibraryUpdated,
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
        await using var assertScope = provider.CreateAsyncScope();
        var rows = await assertScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .AdminRealtimeOutboxEvents
            .ToListAsync();
        rows.ShouldAllBe(row => row.ProcessedAtUtc == null && !string.IsNullOrWhiteSpace(row.LastError));
    }

    private static ServiceProvider CreateServiceProvider(
        PostgreSqlTestDatabase connection,
        IHubContext<JobHub> jobHubContext,
        IHubContext<SystemHub> systemHubContext)
    {
        var services = new ServiceCollection();
        var timeProvider = new ManualTimeProvider(Now);

        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        services.AddSingleton<DashboardStatsCache>();
        services.AddSingleton(Romd.Hosting.IntegrationTests.Infrastructure.TestReferenceCatalog.Create());
        services.AddDbContext<RomdDbContext>(options => options.UseNpgsql(connection.ConnectionString));
        services.AddScoped<AdminRealtimeOutbox>();
        services.AddScoped<IAdminEventOutbox>(provider => provider.GetRequiredService<AdminRealtimeOutbox>());
        services.AddScoped<IAdminRealtimeOutbox>(provider => provider.GetRequiredService<AdminRealtimeOutbox>());
        services.AddSingleton(jobHubContext);
        services.AddSingleton(systemHubContext);
        services.AddScoped<SignalRJobNotifier>();
        services.AddScoped<SignalRStatsNotifier>();
        services.AddScoped<IAdminRealtimeEventSink, SignalRAdminRealtimeEventSink>();

        return services.BuildServiceProvider();
    }

    private static IHubContext<JobHub> CreateJobHubContext()
    {
        return CreateJobHubContext(out _);
    }

    private static IHubContext<JobHub> CreateJobHubContext(out IClientProxy proxy)
    {
        var hubContext = Substitute.For<IHubContext<JobHub>>();
        var clients = Substitute.For<IHubClients>();
        proxy = Substitute.For<IClientProxy>();

        hubContext.Clients.Returns(clients);
        clients.Group(Arg.Any<string>()).Returns(proxy);
        proxy
            .SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return hubContext;
    }

    private static IHubContext<SystemHub> CreateSystemHubContext(out IClientProxy proxy)
    {
        var hubContext = Substitute.For<IHubContext<SystemHub>>();
        var clients = Substitute.For<IHubClients>();
        proxy = Substitute.For<IClientProxy>();

        hubContext.Clients.Returns(clients);
        clients.Group("stats:all").Returns(proxy);
        proxy
            .SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return hubContext;
    }

    private static IHubContext<SystemHub> CreateFailingSystemHubContext()
    {
        var hubContext = Substitute.For<IHubContext<SystemHub>>();
        var clients = Substitute.For<IHubClients>();
        var proxy = Substitute.For<IClientProxy>();

        hubContext.Clients.Returns(clients);
        clients.Group("stats:all").Returns(proxy);
        proxy
            .SendCoreAsync("HealthStatsChanged", Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SignalR unavailable")));

        return hubContext;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
