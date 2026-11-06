using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;
using Romd.Persistence.Identity;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ConsumerSecurityTransactionTests
{
    [Fact]
    public async Task Execute_Success_CommitsBeforeReturningCredential()
    {
        var unit = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        using var cancellation = new CancellationTokenSource();
        unit.BeginTransactionAsync(cancellation.Token).Returns(transaction);
        bool committed = false;
        transaction.CommitAsync(cancellation.Token).Returns(_ => { committed = true; return Task.CompletedTask; });
        var credential = Credential();
        var coordinator = new ConsumerSecurityTransaction(unit, NullLogger<ConsumerSecurityTransaction>.Instance);

        var result = await coordinator.ExecuteAsync<IssuedVfsCredential>(token =>
        {
            token.ShouldBe(cancellation.Token);
            committed.ShouldBeFalse();
            return Task.FromResult<ErrorOr<IssuedVfsCredential>>(credential);
        }, cancellation.Token);

        result.Value.ShouldBeSameAs(credential);
        committed.ShouldBeTrue();
        await transaction.Received(1).CommitAsync(cancellation.Token);
        await transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Execute_BusinessError_DisposesWithoutCommitting()
    {
        var (_, transaction, coordinator) = Setup();

        var result = await coordinator.ExecuteAsync<IssuedVfsCredential>(_ =>
            Task.FromResult<ErrorOr<IssuedVfsCredential>>(VfsConnectionErrors.LimitReached()));

        result.FirstError.ShouldBe(VfsConnectionErrors.LimitReached());
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CallbackFailure_PreservesCauseAndDisposesWithoutCommit(bool cancel)
    {
        var (_, transaction, coordinator) = Setup();
        Exception cause = cancel ? new OperationCanceledException() : new InvalidOperationException("mutation failed");

        var error = await CaptureAsync(() => coordinator.ExecuteAsync<IssuedVfsCredential>(_ =>
            Task.FromException<ErrorOr<IssuedVfsCredential>>(cause)));

        error.ShouldBeSameAs(cause);
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Execute_CancelledAfterStaging_DoesNotCommit()
    {
        var (_, transaction, coordinator) = Setup();
        using var cancellation = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => coordinator.ExecuteAsync<IssuedVfsCredential>(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult<ErrorOr<IssuedVfsCredential>>(Credential());
        }, cancellation.Token));

        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Execute_CommitFailure_PreservesCauseAndDisposes()
    {
        var (_, transaction, coordinator) = Setup();
        var cause = new InvalidOperationException("commit failed");
        transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(cause));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => coordinator.ExecuteAsync<IssuedVfsCredential>(_ =>
            Task.FromResult<ErrorOr<IssuedVfsCredential>>(Credential())));

        error.ShouldBeSameAs(cause);
        await transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Execute_CleanupFailureAfterCommit_ReturnsDurableCredentialAndLogsWithoutSecret()
    {
        var (unit, transaction, _) = Setup();
        var logger = new RecordingLogger();
        var cause = new InvalidOperationException("cleanup failed");
        transaction.DisposeAsync().Returns(new ValueTask(Task.FromException(cause)));
        var coordinator = new ConsumerSecurityTransaction(unit, logger);
        var credential = Credential();

        var result = await coordinator.ExecuteAsync<IssuedVfsCredential>(_ =>
            Task.FromResult<ErrorOr<IssuedVfsCredential>>(credential));

        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        result.Value.ShouldBeSameAs(credential);
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeSameAs(cause);
        warning.Message.ShouldNotContain(credential.Password);
    }

    private static (IUnitOfWork Unit, ITransaction Transaction, ConsumerSecurityTransaction Coordinator) Setup()
    {
        var unit = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        return (unit, transaction, new ConsumerSecurityTransaction(unit, NullLogger<ConsumerSecurityTransaction>.Instance));
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

    private static IssuedVfsCredential Credential() => new(
        new VfsConnectionDto(Guid.NewGuid(), "Deck", "romd-user", "Active", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, null, "stored"), "secret-never-to-be-logged");

    private sealed class RecordingLogger : ILogger<ConsumerSecurityTransaction>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
