using ErrorOr;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Application.Common.Security.Commands.StartAccountSession;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Security;

public sealed class StartAccountSessionCommandTests
{
    [Theory]
    [InlineData("2026-11-04T12:00:00.123456Z")]
    [InlineData("2026-11-04T12:00:00.123456+05:30")]
    public async Task Handle_CapturedFacts_UsesOneClockFactAndAbsoluteThirtyDayExpiry(string instant)
    {
        var now = DateTimeOffset.Parse(instant);
        var clock = new CountingClock(now);
        var boundary = Substitute.For<IAccountSessionTransaction>();
        var creations = Substitute.For<IAccountSessionCreationStore>();
        bool inside = false;
        using var cancellation = new CancellationTokenSource();
        var user = Guid.NewGuid();
        AccountSessionCreationFacts? staged = null;
        boundary.ExecuteAsync(Arg.Any<Func<CancellationToken, Task<ErrorOr<AccountSession>>>>(), cancellation.Token).Returns(async call =>
        {
            inside = true;
            try
            { return await call.Arg<Func<CancellationToken, Task<ErrorOr<AccountSession>>>>()(cancellation.Token); }
            finally { inside = false; }
        });
        creations.StageStartAsync(Arg.Any<AccountSessionCreationFacts>(), cancellation.Token).Returns(call =>
        {
            inside.ShouldBeTrue();
            staged = call.Arg<AccountSessionCreationFacts>();
            return Describe(staged);
        });

        var result = await new StartAccountSessionCommandHandler(creations, boundary, clock)
            .HandleAsync(new(user, "captured-client", "  Browser\n on Linux  ", "captured-stamp"), cancellation.Token);

        result.IsError.ShouldBeFalse();
        staged.ShouldNotBeNull().Id.ShouldNotBe(Guid.Empty);
        staged.UserId.ShouldBe(user);
        staged.ClientId.ShouldBe("captured-client");
        staged.Device.ShouldBe("Browser on Linux");
        staged.AccountStamp.ShouldBe("captured-stamp");
        staged.CreatedAt.ShouldBe(now);
        staged.CreatedAt.Offset.ShouldBe(now.Offset);
        staged.ExpiresAt.ShouldBe(now.AddDays(30));
        result.Value.CreatedAt.ShouldBe(now);
        result.Value.LastUsedAt.ShouldBe(now);
        result.Value.ExpiresAt.ShouldBe(now.AddDays(30));
        result.Value.Status.ShouldBe("Current");
        clock.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("  Device  ", "Device")]
    [InlineData("De\0vi\nce\t", "Device")]
    [InlineData("   ", "")]
    [InlineData("Console • 日本語", "Console • 日本語")]
    public void DeviceLabel_ControlsAndWhitespace_PreservesExistingSanitization(string raw, string expected) =>
        AccountSessionDeviceLabel.Clean(raw).ShouldBe(expected);

    [Fact]
    public void DeviceLabel_TruncatesBeforeTrimming_PreservesExistingCharacterBoundary()
    {
        AccountSessionDeviceLabel.Clean("  " + new string('a', 119) + "Z").ShouldBe(new string('a', 118));
        AccountSessionDeviceLabel.Clean(new string('x', 121)).ShouldBe(new string('x', 120));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_StagingFaultOrCancellation_PreservesCause(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("staging failed");
        var creations = Substitute.For<IAccountSessionCreationStore>();
        creations.StageStartAsync(Arg.Any<AccountSessionCreationFacts>(), cancellation.Token).Returns<Task<AccountSession>>(_ => throw cause);
        var handler = new StartAccountSessionCommandHandler(creations, InlineBoundary(), new CountingClock(DateTimeOffset.UnixEpoch));

        var error = await CaptureAsync(() => handler.HandleAsync(new(Guid.NewGuid(), "client", "Device", "stamp"), cancellation.Token));

        error.ShouldBeSameAs(cause);
    }

    [Fact]
    public async Task Handle_AcceptanceIncomplete_DoesNotExposePreparedSessionEarly()
    {
        var boundary = Substitute.For<IAccountSessionTransaction>();
        var gate = new TaskCompletionSource<ErrorOr<AccountSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.ExecuteAsync(Arg.Any<Func<CancellationToken, Task<ErrorOr<AccountSession>>>>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var handler = new StartAccountSessionCommandHandler(Substitute.For<IAccountSessionCreationStore>(), boundary,
            new CountingClock(DateTimeOffset.UnixEpoch));

        var pending = handler.HandleAsync(new(Guid.NewGuid(), "client", "Device", "stamp"));

        pending.IsCompleted.ShouldBeFalse();
        var accepted = Describe(new(Guid.NewGuid(), Guid.NewGuid(), "client", "Device", "stamp", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30)));
        gate.SetResult(accepted);
        (await pending).Value.ShouldBe(accepted);
    }

    [Fact]
    public async Task Handle_RepeatedSignIns_CreateDistinctSessionsWithoutDeduplication()
    {
        var creations = Substitute.For<IAccountSessionCreationStore>();
        creations.StageStartAsync(Arg.Any<AccountSessionCreationFacts>(), Arg.Any<CancellationToken>()).Returns(call => Describe(call.Arg<AccountSessionCreationFacts>()));
        var handler = new StartAccountSessionCommandHandler(creations, InlineBoundary(), new CountingClock(DateTimeOffset.UnixEpoch));
        var command = new StartAccountSessionCommand(Guid.NewGuid(), "client", "Device", "stamp");

        var first = await handler.HandleAsync(command);
        var second = await handler.HandleAsync(command);

        second.Value.Id.ShouldNotBe(first.Value.Id);
        second.Value.UserId.ShouldBe(first.Value.UserId);
        second.Value.ExpiresAt.ShouldBe(first.Value.ExpiresAt);
    }

    private static IAccountSessionTransaction InlineBoundary()
    {
        // Tests command decisions; real transaction truth is covered by PostgreSQL tests.
        var boundary = Substitute.For<IAccountSessionTransaction>();
        boundary.ExecuteAsync(Arg.Any<Func<CancellationToken, Task<ErrorOr<AccountSession>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<ErrorOr<AccountSession>>>>()(call.Arg<CancellationToken>()));
        return boundary;
    }
    private static AccountSession Describe(AccountSessionCreationFacts facts) => new(facts.Id, facts.UserId, facts.ClientId,
        facts.Device, facts.CreatedAt, facts.CreatedAt, facts.ExpiresAt, null, null, null, "Current");
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        { await action(); return null; }
        catch (Exception error) { return error; }
    }
    private sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        public int Calls { get; private set; }
        public override DateTimeOffset GetUtcNow() { Calls++; return now; }
    }
}
