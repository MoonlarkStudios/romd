using ErrorOr;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Account.Commands.ChangeConsumerPassword;
using Romd.Consumer.Application.Common.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Account;

public sealed class ChangeConsumerPasswordCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_MissingCurrentUser_DoesNotPrepareOrOpenTransaction(bool authenticated)
    {
        var current = Substitute.For<ICurrentUser>();
        current.IsAuthenticated.Returns(authenticated);
        var password = Substitute.For<IConsumerPasswordChanger>();
        var transaction = new TestTransaction();

        var result = await new ChangeConsumerPasswordCommandHandler(current, password, transaction)
            .HandleAsync(new("current", "replacement"));

        result.FirstError.ShouldBe(ConsumerErrors.CurrentUserRequired());
        password.ReceivedCalls().ShouldBeEmpty();
        transaction.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("", "replacement")]
    [InlineData("current", " ")]
    public async Task Handle_MissingPassword_DoesNotPrepareOrOpenTransaction(string current, string replacement)
    {
        var password = Substitute.For<IConsumerPasswordChanger>();
        var transaction = new TestTransaction();

        var result = await Handler(password, transaction).HandleAsync(new(current, replacement));

        result.FirstError.ShouldBe(ConsumerErrors.PasswordChangeFailed("Current password and new password are required."));
        password.ReceivedCalls().ShouldBeEmpty();
        transaction.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_PreparationRejected_PreservesErrorWithoutOpeningTransaction()
    {
        var password = Substitute.For<IConsumerPasswordChanger>();
        var rejection = ConsumerErrors.PasswordChangeFailed("Current password is incorrect.");
        password.PrepareAsync(Arg.Any<Guid>(), "current", "replacement", Arg.Any<CancellationToken>()).Returns(rejection);
        var transaction = new TestTransaction();

        var result = await Handler(password, transaction).HandleAsync(new("current", "replacement"));

        result.FirstError.ShouldBe(rejection);
        transaction.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_PreparationFails_PreservesCauseWithoutOpeningTransaction(bool cancel)
    {
        var password = Substitute.For<IConsumerPasswordChanger>();
        Exception cause = cancel ? new OperationCanceledException() : new IOException("preparation failed");
        password.PrepareAsync(Arg.Any<Guid>(), "current", "replacement", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ErrorOr<IPreparedConsumerPasswordChange>>(cause));
        var transaction = new TestTransaction();

        var error = await CaptureAsync(() => Handler(password, transaction).HandleAsync(new("current", "replacement")));

        error.ShouldBeSameAs(cause);
        transaction.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_ValidChange_PreparesOutsideTransactionAndReturnsOnlyAfterCommit()
    {
        var password = Substitute.For<IConsumerPasswordChanger>();
        var prepared = Substitute.For<IPreparedConsumerPasswordChange>();
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new TestTransaction { BeforeCommit = commit.Task };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var userId = Guid.NewGuid();
        password.PrepareAsync(userId, "current", "replacement", cancellation.Token).Returns(_ =>
        {
            transaction.Calls.ShouldBe(0);
            transaction.Active.ShouldBeFalse();
            return Task.FromResult(ErrorOrFactory.From(prepared));
        });
        prepared.ApplyStagedAsync(cancellation.Token).Returns(_ =>
        {
            transaction.Active.ShouldBeTrue();
            apply.SetResult();
            return Task.FromResult<ErrorOr<Updated>>(Result.Updated);
        });

        var pending = Handler(password, transaction, userId).HandleAsync(new("current", "replacement"), cancellation.Token);
        try
        {
            var ready = await Task.WhenAny(apply.Task, pending).WaitAsync(cancellation.Token);
            if (ready == pending)
                await pending;
            pending.IsCompleted.ShouldBeFalse();
            transaction.Committed.ShouldBeFalse();
        }
        finally
        {
            commit.TrySetResult();
        }

        (await pending).Value.ShouldBe(Result.Updated);
        transaction.Calls.ShouldBe(1);
        transaction.Committed.ShouldBeTrue();
        await password.Received(1).PrepareAsync(userId, "current", "replacement", cancellation.Token);
        await prepared.Received(1).ApplyStagedAsync(cancellation.Token);
    }

    [Fact]
    public async Task Handle_ApplyRejected_PreservesErrorWithoutCommitting()
    {
        var password = Substitute.For<IConsumerPasswordChanger>();
        var prepared = Substitute.For<IPreparedConsumerPasswordChange>();
        password.PrepareAsync(Arg.Any<Guid>(), "current", "replacement", Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(prepared));
        var rejection = ConsumerErrors.PasswordChangeFailed("The account changed while updating the password. Try again.");
        prepared.ApplyStagedAsync(Arg.Any<CancellationToken>()).Returns(rejection);
        var transaction = new TestTransaction();

        var result = await Handler(password, transaction).HandleAsync(new("current", "replacement"));

        result.FirstError.ShouldBe(rejection);
        transaction.Calls.ShouldBe(1);
        transaction.Committed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_ApplyFails_PreservesCauseWithoutCommitting(bool cancel)
    {
        var password = Substitute.For<IConsumerPasswordChanger>();
        var prepared = Substitute.For<IPreparedConsumerPasswordChange>();
        password.PrepareAsync(Arg.Any<Guid>(), "current", "replacement", Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(prepared));
        Exception cause = cancel ? new OperationCanceledException() : new IOException("mutation failed");
        prepared.ApplyStagedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<ErrorOr<Updated>>(cause));
        var transaction = new TestTransaction();

        var error = await CaptureAsync(() => Handler(password, transaction).HandleAsync(new("current", "replacement")));

        error.ShouldBeSameAs(cause);
        transaction.Calls.ShouldBe(1);
        transaction.Committed.ShouldBeFalse();
    }

    private static ChangeConsumerPasswordCommandHandler Handler(IConsumerPasswordChanger password,
        IConsumerSecurityTransaction transaction, Guid? userId = null)
    {
        var current = Substitute.For<ICurrentUser>();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(userId ?? Guid.NewGuid());
        return new ChangeConsumerPasswordCommandHandler(current, password, transaction);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class TestTransaction : IConsumerSecurityTransaction
    {
        public int Calls { get; private set; }
        public bool Active { get; private set; }
        public bool Committed { get; private set; }
        public Task BeforeCommit { get; init; } = Task.CompletedTask;

        public async Task<ErrorOr<T>> ExecuteAsync<T>(Func<CancellationToken, Task<ErrorOr<T>>> mutation, CancellationToken ct = default)
        {
            Calls++;
            Active = true;
            try
            {
                var result = await mutation(ct);
                if (!result.IsError)
                {
                    await BeforeCommit.WaitAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    Committed = true;
                }
                return result;
            }
            finally
            {
                Active = false;
            }
        }
    }
}
