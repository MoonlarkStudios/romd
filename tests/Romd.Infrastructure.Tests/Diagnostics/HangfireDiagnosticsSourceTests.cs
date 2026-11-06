using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Diagnostics;
using Romd.Infrastructure.Diagnostics;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Diagnostics;

public sealed class HangfireDiagnosticsSourceTests
{
    [Fact]
    public void Read_AvailableProvider_BoundsWholeServerSnapshotAndRecurringIds()
    {
        var monitoring = Substitute.For<IMonitoringApi>();
        monitoring.Servers().Returns([
            new ServerDto
            {
                Name = "worker-b",
                WorkersCount = 3,
                Queues = ["upload"],
                StartedAt = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc),
                Heartbeat = new DateTime(2026, 8, 26, 11, 59, 0, DateTimeKind.Utc)
            },
            new ServerDto
            {
                Name = "worker-a",
                WorkersCount = 2,
                Queues = ["default"]
            }
        ]);
        monitoring.EnqueuedCount("default").Returns(4);
        monitoring.FetchedCount("default").Returns(1);
        var connection = Substitute.For<JobStorageConnection>();
        connection.GetRangeFromSet("recurring-jobs", 0, 6).Returns([]);
        var storage = Substitute.For<JobStorage>();
        storage.GetMonitoringApi().Returns(monitoring);
        storage.GetConnection().Returns(connection);
        using var services = new ServiceCollection().AddSingleton(storage).BuildServiceProvider();

        var result = new HangfireDiagnosticsSource(services)
            .Read(serverTake: 1, recurringJobTake: 7, queues: ["default"]);

        result.Servers.ShouldHaveSingleItem().Name.ShouldBe("worker-a");
        result.Servers[0].WorkerCount.ShouldBe(2);
        result.RecurringJobs.ShouldBeEmpty();
        result.QueueBacklogs.ShouldHaveSingleItem().ShouldBe(
            new QueueBacklogDiagnosticsData("default", 4, 1));
        connection.Received(1).GetRangeFromSet("recurring-jobs", 0, 6);
        monitoring.Received(1).Servers();
    }

    [Fact]
    public void Read_ProviderWithoutBoundedConnection_ThrowsNamedUnavailableReason()
    {
        var storage = Substitute.For<JobStorage>();
        storage.GetMonitoringApi().Returns(Substitute.For<IMonitoringApi>());
        storage.GetConnection().Returns(Substitute.For<IStorageConnection>());
        using var services = new ServiceCollection().AddSingleton(storage).BuildServiceProvider();

        var exception = Should.Throw<InvalidOperationException>(() =>
            new HangfireDiagnosticsSource(services)
                .Read(serverTake: 1, recurringJobTake: 1, queues: []));

        exception.Message.ShouldContain("bounded recurring-job reads");
    }
}
