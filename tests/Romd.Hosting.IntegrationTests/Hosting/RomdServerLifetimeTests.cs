using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Hosting;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class RomdServerLifetimeTests
{
    [Fact]
    public async Task RunRomdServerAsync_StopRequestedDuringStartup_CompletesAndDisposesHost()
    {
        var observed = new Observation();
        var host = CreateHost(Behavior.StopDuringStartup, observed);

        await host.RunRomdServerAsync();

        observed.StopRequested.ShouldBeTrue();
        observed.Started.ShouldBeFalse();
        observed.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task RunRomdServerAsync_StartedServerStopsNormally_CompletesAndDisposesHost()
    {
        var observed = new Observation();
        var host = CreateHost(Behavior.Normal, observed);

        await host.RunRomdServerAsync();

        observed.Started.ShouldBeTrue();
        observed.Stopped.ShouldBeTrue();
        observed.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task RunRomdServerAsync_UnrequestedStartupCancellation_PreservesFailureAndDisposesHost()
    {
        var observed = new Observation();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var expected = new OperationCanceledException(canceled.Token);
        var host = CreateHost(Behavior.StartupFailure, observed, expected);

        var failure = await CaptureCancellationAsync(host);

        failure.ShouldBeSameAs(expected);
        failure!.CancellationToken.ShouldBe(canceled.Token);
        observed.StopRequested.ShouldBeFalse();
        observed.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(Behavior.StartupFailure)]
    [InlineData(Behavior.StartupFailureAfterStop)]
    public async Task RunRomdServerAsync_UnexpectedStartupFailure_IsNotWaivedByStop(Behavior behavior)
    {
        var observed = new Observation();
        var expected = new InvalidOperationException("Startup failed independently of a stop request.");
        var host = CreateHost(behavior, observed, expected);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => host.RunRomdServerAsync());

        failure.ShouldBeSameAs(expected);
        observed.StopRequested.ShouldBe(behavior == Behavior.StartupFailureAfterStop);
        observed.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task RunRomdServerAsync_StartedServerStopCancellation_PreservesFailureAndDisposesHost()
    {
        var observed = new Observation();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var expected = new OperationCanceledException(canceled.Token);
        var host = CreateHost(Behavior.ShutdownCancellation, observed, expected);

        var failure = await CaptureCancellationAsync(host);

        failure.ShouldBeSameAs(expected);
        failure!.CancellationToken.ShouldBe(canceled.Token);
        observed.Started.ShouldBeTrue();
        observed.Stopped.ShouldBeTrue();
        observed.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task RunRomdServerAsync_StoppedStartupDisposalCancellation_PreservesIndependentDisposalFailure()
    {
        var observed = new Observation();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var expected = new OperationCanceledException(canceled.Token);
        var host = CreateHost(Behavior.StoppedStartupDisposalCancellation, observed, expected);

        var failure = await CaptureCancellationAsync(host);

        failure.ShouldBeSameAs(expected);
        failure!.CancellationToken.ShouldBe(canceled.Token);
        observed.StopRequested.ShouldBeTrue();
        observed.Started.ShouldBeFalse();
        observed.DisposeCount.ShouldBe(1);
    }

    private static async Task<OperationCanceledException?> CaptureCancellationAsync(IHost host)
    {
        // Direct observation preserves cancellation identity; Should.ThrowAsync
        // can replace it with TaskCanceledException (documented known issue).
        try
        { await host.RunRomdServerAsync(); }
        catch (OperationCanceledException failure) { return failure; }
        return null;
    }

    private static IHost CreateHost(Behavior behavior, Observation observed, Exception? failure = null) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostLifetime, QuietLifetime>();
                services.AddSingleton<IHostedService>(provider => new ControlledService(
                    provider.GetRequiredService<IHostApplicationLifetime>(), behavior, observed, failure));
            })
            .Build();

    public enum Behavior
    {
        Normal,
        StopDuringStartup,
        StartupFailure,
        StartupFailureAfterStop,
        ShutdownCancellation,
        StoppedStartupDisposalCancellation
    }

    private sealed class Observation
    {
        public bool Started { get; set; }
        public bool StopRequested { get; set; }
        public bool Stopped { get; set; }
        public int DisposeCount { get; set; }
    }

    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ControlledService(
        IHostApplicationLifetime lifetime,
        Behavior behavior,
        Observation observed,
        Exception? failure) : IHostedService, IDisposable, IAsyncDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (behavior is Behavior.StopDuringStartup or Behavior.StartupFailureAfterStop or Behavior.StoppedStartupDisposalCancellation)
            {
                observed.StopRequested = true;
                lifetime.StopApplication();
            }
            if (behavior is Behavior.StopDuringStartup or Behavior.StoppedStartupDisposalCancellation)
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (behavior is Behavior.StartupFailure or Behavior.StartupFailureAfterStop)
                return Task.FromException(failure!);
            lifetime.ApplicationStarted.Register(() =>
            {
                observed.Started = true;
                observed.StopRequested = true;
                lifetime.StopApplication();
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            observed.Stopped = true;
            return behavior == Behavior.ShutdownCancellation ? Task.FromException(failure!) : Task.CompletedTask;
        }

        public void Dispose() => observed.DisposeCount++;

        public ValueTask DisposeAsync()
        {
            observed.DisposeCount++;
            return behavior == Behavior.StoppedStartupDisposalCancellation
                ? ValueTask.FromException(failure!) : ValueTask.CompletedTask;
        }
    }
}
