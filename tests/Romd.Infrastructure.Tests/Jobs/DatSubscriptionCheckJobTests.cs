using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Source.Dat;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class DatSubscriptionCheckJobTests
{
    [Fact]
    public async Task ExecuteAsync_OneCheckThrows_RecordsFailureInFreshScopeAndContinues()
    {
        var instances = new List<IDatCatalogEnrollmentService>();
        var checkedIds = new List<int>();
        var recordedIds = new List<int>();
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var service = Substitute.For<IDatCatalogEnrollmentService>();
            instances.Add(service);
            service.GetDueCheckIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 1, 2 });
            service.CheckScheduledAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var id = call.Arg<int>(); checkedIds.Add(id);
                if (id == 1) throw new IOException("provider failed");
                return new DatCatalogSubscription(id, "redump/psx/discs", "psx", "PlayStation", 1, null,
                    "ReadyToImport", null, null, "hash", null);
            });
            service.RecordScheduledFailureAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => { recordedIds.Add(call.Arg<int>()); return Task.CompletedTask; });
            return service;
        });
        await using var provider = services.BuildServiceProvider();
        var job = new DatSubscriptionCheckJob(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatSubscriptionCheckJob>.Instance);
        await job.ExecuteAsync(default);
        checkedIds.ShouldBe(new[] { 1, 2 }); recordedIds.ShouldBe(new[] { 1 });
        instances.Count.ShouldBe(4); // discovery, failed check, failure recording, next check
        await instances[2].Received(1).RecordScheduledFailureAsync(1, Arg.Any<CancellationToken>());
        foreach (var service in instances)
            await service.DidNotReceive().ApplyAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
