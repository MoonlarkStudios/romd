using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Account.Commands.UpdateConsumerUserSettings;
using Romd.Consumer.Application.Browse;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Account;

public sealed class UpdateConsumerUserSettingsSettlementTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_ResponseReadFails_PreservesCauseAndNeverWrites(bool language, bool cancel)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("response cancellation", cancellation.Token)
            : new IOException("response read failed");
        var failed = Task.FromException<IReadOnlyDictionary<int, string>>(cause);
        if (language)
            fixture.Keys.LanguageKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(failed);
        else
            fixture.Keys.RegionKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(failed);

        var error = await CaptureAsync(() => fixture.Handler.HandleAsync(new("dark", null), cancellation.Token));

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        await fixture.Store.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default, default!, default);
    }

    [Fact]
    public async Task Handle_SaveAcknowledgedAfterCancellation_ReturnsPreparedResponseWithoutMoreReads()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        bool saved = false;
        fixture.Keys.RegionKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            saved.ShouldBeFalse();
            return new Dictionary<int, string> { [3] = "europe" };
        });
        fixture.Keys.LanguageKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            saved.ShouldBeFalse();
            return new Dictionary<int, string> { [7] = "en" };
        });
        fixture.Store.SaveSettingsAsync(fixture.UserId, Arg.Any<ConsumerAccountSettings>(), cancellation.Token).Returns(call =>
        {
            saved = true;
            cancellation.Cancel();
            return call.Arg<ConsumerAccountSettings>();
        });

        var result = await fixture.Handler.HandleAsync(new("dark", null), cancellation.Token);

        saved.ShouldBeTrue();
        result.Value.Theme.ShouldBe("dark");
        result.Value.ReleasePreference.PreferredRegionKeys.ShouldBe(["europe"]);
        result.Value.ReleasePreference.PreferredLanguageKeys.ShouldBe(["en"]);
        result.Value.ReleasePreference.RevisionStrategy.ShouldBe("newestFirst");
        await fixture.Keys.Received(1).RegionKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), cancellation.Token);
        await fixture.Keys.Received(1).LanguageKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), cancellation.Token);
        await fixture.Store.Received(1).SaveSettingsAsync(fixture.UserId, Arg.Any<ConsumerAccountSettings>(), cancellation.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_SaveAcknowledgementFails_DoesNotReturnPreparedSuccessOrRetry(bool cancel)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("uncertain acknowledgement", cancellation.Token)
            : new IOException("uncertain acknowledgement");
        fixture.Store.SaveSettingsAsync(fixture.UserId, Arg.Any<ConsumerAccountSettings>(), cancellation.Token)
            .Returns(Task.FromException<ConsumerAccountSettings>(cause));

        var error = await CaptureAsync(() => fixture.Handler.HandleAsync(new("dark", null), cancellation.Token));

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        await fixture.Store.Received(1).SaveSettingsAsync(fixture.UserId, Arg.Any<ConsumerAccountSettings>(), cancellation.Token);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Fixture
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public IConsumerUserSettingsStore Store { get; } = Substitute.For<IConsumerUserSettingsStore>();
        public IConsumerTaxonomyKeys Keys { get; } = Substitute.For<IConsumerTaxonomyKeys>();
        public UpdateConsumerUserSettingsCommandHandler Handler { get; }

        public Fixture()
        {
            var user = Substitute.For<ICurrentUser>();
            user.IsAuthenticated.Returns(true);
            user.UserId.Returns(UserId);
            Store.GetSettingsAsync(UserId, Arg.Any<CancellationToken>())
                .Returns(new ConsumerAccountSettings("system", new([3], [7], ConsumerRevisionPreference.NewestFirst)));
            Keys.RegionKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<int, string> { [3] = "europe" });
            Keys.LanguageKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<int, string> { [7] = "en" });
            Handler = new(user, Store, Keys);
        }
    }
}
