using Microsoft.Extensions.DependencyInjection;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Source.Dat.Commands.CheckDatSubscription;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Source.Dat;
using Romd.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class DatSubscriptionCheckJobTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OneCheckThrows_RecordsFailureInFreshScopeAndContinues(bool deadlineCancellation)
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
            service.RecordScheduledFailureAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => { recordedIds.Add(call.Arg<int>()); return Task.CompletedTask; });
            return service;
        });
        services.AddScoped<ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription>>(scope =>
        {
            _ = scope.GetRequiredService<IDatCatalogEnrollmentService>();
            var handler = Substitute.For<ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription>>();
            handler.HandleAsync(Arg.Any<CheckDatSubscriptionCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var command = call.Arg<CheckDatSubscriptionCommand>();
                command.Mode.ShouldBe(DatSubscriptionCheckMode.Scheduled);
                checkedIds.Add(command.SubscriptionId);
                if (command.SubscriptionId == 1)
                    throw deadlineCancellation ? new OperationCanceledException(new CancellationToken(true)) : new IOException("provider failed");
                return new DatCatalogSubscription(command.SubscriptionId, "redump/psx/discs", "psx", "PlayStation", 1, null,
                    "ReadyToImport", null, null, "hash", null);
            });
            return handler;
        });
        await using var provider = services.BuildServiceProvider();
        var job = new DatSubscriptionCheckJob(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatSubscriptionCheckJob>.Instance);
        await job.ExecuteAsync(default);
        checkedIds.ShouldBe(new[] { 1, 2 });
        recordedIds.ShouldBe(new[] { 1 });
        instances.Count.ShouldBe(4); // discovery, failed check, failure recording, next check
        await instances[2].Received(1).RecordScheduledFailureAsync(1, Arg.Any<CancellationToken>());
        foreach (var service in instances)
            service.ReceivedCalls().ShouldAllBe(call => call.GetMethodInfo().Name == nameof(IDatCatalogEnrollmentService.GetDueCheckIdsAsync)
                || call.GetMethodInfo().Name == nameof(IDatCatalogEnrollmentService.RecordScheduledFailureAsync));
    }
    [Theory]
    [InlineData("DatEnrollment.Busy", false)]
    [InlineData("DatEnrollment.NotFound", false)]
    [InlineData("DatEnrollment.PlatformMissing", true)]
    public async Task ExecuteAsync_ExpectedCheckRejection_RecordsOnlyEligibleFailureInFreshScope(string code, bool recordsFailure)
    {
        var services = new ServiceCollection();
        var instances = new List<IDatCatalogEnrollmentService>();
        var checkedInstance = new List<IDatCatalogEnrollmentService>();
        services.AddScoped(_ =>
        {
            var service = Substitute.For<IDatCatalogEnrollmentService>();
            instances.Add(service);
            service.GetDueCheckIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 7 });
            return service;
        });
        services.AddScoped<ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription>>(scope =>
        {
            checkedInstance.Add(scope.GetRequiredService<IDatCatalogEnrollmentService>());
            var handler = Substitute.For<ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription>>();
            handler.HandleAsync(new CheckDatSubscriptionCommand(7, DatSubscriptionCheckMode.Scheduled), Arg.Any<CancellationToken>())
                .Returns(Error.Conflict(code, "check rejected"));
            return handler;
        });
        await using var provider = services.BuildServiceProvider();

        await new DatSubscriptionCheckJob(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatSubscriptionCheckJob>.Instance)
            .ExecuteAsync(default);

        instances.Count.ShouldBe(recordsFailure ? 3 : 2);
        foreach (var service in instances.Take(2))
            await service.DidNotReceive().RecordScheduledFailureAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        if (recordsFailure)
        {
            instances[2].ShouldNotBeSameAs(checkedInstance.Single());
            await instances[2].Received(1).RecordScheduledFailureAsync(7, Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task ExecuteAsync_HostCancellation_PropagatesWithoutFailureRecording()
    {
        using var cancellation = new CancellationTokenSource();
        var services = new ServiceCollection();
        var instances = new List<IDatCatalogEnrollmentService>();
        services.AddScoped(_ =>
        {
            var service = Substitute.For<IDatCatalogEnrollmentService>();
            instances.Add(service);
            service.GetDueCheckIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 7 });
            return service;
        });
        services.AddScoped<ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription>>(scope =>
        {
            _ = scope.GetRequiredService<IDatCatalogEnrollmentService>();
            var handler = Substitute.For<ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription>>();
            handler.HandleAsync(Arg.Any<CheckDatSubscriptionCommand>(), Arg.Any<CancellationToken>())
                .Returns<Task<ErrorOr<DatCatalogSubscription>>>(_ =>
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                });
            return handler;
        });
        await using var provider = services.BuildServiceProvider();
        var job = new DatSubscriptionCheckJob(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatSubscriptionCheckJob>.Instance);

        var error = await Should.ThrowAsync<OperationCanceledException>(() => job.ExecuteAsync(cancellation.Token));

        error.CancellationToken.ShouldBe(cancellation.Token);
        instances.Count.ShouldBe(2);
        foreach (var service in instances)
            await service.DidNotReceive().RecordScheduledFailureAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

}
