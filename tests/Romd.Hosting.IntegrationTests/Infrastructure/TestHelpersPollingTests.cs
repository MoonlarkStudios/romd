using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>Pure polling tests: no host, worker, database or system-clock adjustment.</summary>
public sealed class TestHelpersPollingTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task WaitForJobCompletionAsync_UtcJumpsWithinElapsedBudget_ContinuesUntilCompleted(int utcJumpMinutes)
    {
        var clock = new PollingClock();
        int requests = 0;
        using var client = Client(() =>
        {
            requests++;
            if (requests == 1)
            {
                clock.AdvanceUtc(TimeSpan.FromMinutes(utcJumpMinutes));
                clock.AdvanceElapsed(TimeSpan.FromSeconds(1));
                return "Pending";
            }
            return "Completed";
        });

        await TestHelpers.WaitForJobCompletionAsync(client, "job", timeProvider: clock);

        requests.ShouldBe(2);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task WaitForJobCompletionAsync_ElapsedDeadlineWithUtcJump_TimesOutWithoutAnotherPoll(int utcJumpMinutes)
    {
        var clock = new PollingClock();
        int requests = 0;
        using var client = Client(() =>
        {
            if (++requests > 1)
                throw new InvalidOperationException("The elapsed deadline must prevent another poll.");
            clock.AdvanceUtc(TimeSpan.FromMinutes(utcJumpMinutes));
            clock.AdvanceElapsed(TimeSpan.FromSeconds(30));
            return "Pending";
        });

        var error = await Should.ThrowAsync<TimeoutException>(() =>
            TestHelpers.WaitForJobCompletionAsync(client, "job", timeProvider: clock));

        requests.ShouldBe(1);
        error.Message.ShouldContain("within 30 seconds");
        error.Message.ShouldContain("Last phase: Pending");
    }

    [Fact]
    public async Task WaitForJobCompletionAsync_CancelledBeforePolling_PreservesCancellationWithoutRequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int requests = 0;
        using var client = Client(() => { requests++; return "Completed"; });

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            TestHelpers.WaitForJobCompletionAsync(client, "job", cancellationToken: cancellation.Token, timeProvider: new PollingClock()));

        requests.ShouldBe(0);
        error.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact]
    public async Task WaitForJobCompletionAsync_CancelledDuringPolling_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        int requests = 0;
        using var client = Client(() => { requests++; cancellation.Cancel(); return "Pending"; });

        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            TestHelpers.WaitForJobCompletionAsync(client, "job", cancellationToken: cancellation.Token, timeProvider: new PollingClock()));

        requests.ShouldBe(1);
        error.CancellationToken.ShouldBe(cancellation.Token);
    }

    private static HttpClient Client(Func<string> nextPhase) => new(new StatusHandler(nextPhase))
    {
        BaseAddress = new Uri("http://polling-fixture.invalid")
    };

    private sealed class StatusHandler(Func<string> nextPhase) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { phase = nextPhase(), currentItem = (string?)null })
            });
    }

    private sealed class PollingClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceUtc(TimeSpan delta) => _utc += delta;
        public void AdvanceElapsed(TimeSpan delta) => _timestamp += delta.Ticks;
    }
}
