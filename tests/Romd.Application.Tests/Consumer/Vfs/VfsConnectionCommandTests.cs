using ErrorOr;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Consumer.Application.Vfs;
using Romd.Consumer.Application.Vfs.Commands.CreateVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RenameVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RotateVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RevokeVfsConnection;
using Romd.Contracts.Consumer.Account;
using Romd.Domain.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Vfs;

public sealed class VfsConnectionCommandTests
{
    [Theory]
    [InlineData(VfsFormat.Stored)]
    [InlineData(VfsFormat.Original)]
    public async Task Create_ValidFormat_PassesTypedSelectionForCurrentUser(VfsFormat format)
    {
        var current = Substitute.For<ICurrentUser>();
        var userId = Guid.NewGuid();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(userId);
        var connections = Substitute.For<IVfsConnections>();
        var credential = new IssuedVfsCredential(Dto(format), "test-secret");
        connections.CreateAsync(userId, "Device", Arg.Any<CancellationToken>(), format).Returns(credential);
        var result = await new CreateVfsConnectionCommandHandler(current, connections, new TestTransaction())
            .HandleAsync(new(" Device ", format));
        result.Value.ShouldBe(credential);
        await connections.Received(1).CreateAsync(userId, "Device", Arg.Any<CancellationToken>(), format);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commands_InvalidFormat_DoNotCallStore(bool create)
    {
        var current = Substitute.For<ICurrentUser>();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(Guid.NewGuid());
        var connections = Substitute.For<IVfsConnections>();
        var error = create
            ? (await new CreateVfsConnectionCommandHandler(current, connections, new TestTransaction()).HandleAsync(new("Device", (VfsFormat)99))).FirstError
            : (await new RenameVfsConnectionCommandHandler(current, connections).HandleAsync(new(Guid.NewGuid(), "Device", (VfsFormat)99))).FirstError;
        error.ShouldBe(VfsConnectionErrors.InvalidFormat());
        connections.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Rename_OmittedFormat_PassesNoSelectionToPreserveStoredPreference()
    {
        var current = Substitute.For<ICurrentUser>();
        var userId = Guid.NewGuid();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(userId);
        var connections = Substitute.For<IVfsConnections>();
        var dto = Dto(VfsFormat.Original);
        connections.RenameAsync(userId, dto.Id, "Renamed", Arg.Any<CancellationToken>(), null).Returns(dto);
        var result = await new RenameVfsConnectionCommandHandler(current, connections)
            .HandleAsync(new(dto.Id, "Renamed"));
        result.Value.Format.ShouldBe("original");
        await connections.Received(1).RenameAsync(userId, dto.Id, "Renamed", Arg.Any<CancellationToken>(), null);
    }

    [Fact]
    public async Task Commands_Unauthenticated_DoNotCallStore()
    {
        var current = Substitute.For<ICurrentUser>();
        var connections = Substitute.For<IVfsConnections>();
        (await new CreateVfsConnectionCommandHandler(current, connections, new TestTransaction()).HandleAsync(new("Device")))
            .FirstError.ShouldBe(ConsumerErrors.CurrentUserRequired());
        (await new RenameVfsConnectionCommandHandler(current, connections).HandleAsync(new(Guid.NewGuid(), "Device", VfsFormat.Original)))
            .FirstError.ShouldBe(ConsumerErrors.CurrentUserRequired());
        connections.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rotate")]
    [InlineData("revoke")]
    public async Task CredentialMutation_ValidRequest_ReturnsOnlyAfterTransactionCommits(string operation)
    {
        var current = CurrentUser();
        var connections = Substitute.For<IVfsConnections>();
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new TestTransaction { BeforeCommit = commit.Task };
        var credential = new IssuedVfsCredential(Dto(VfsFormat.Stored), "issued-only-after-commit");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        connections.CreateAsync(current.UserId!.Value, "Device", cancellation.Token, VfsFormat.Stored)
            .Returns(_ => Stage(credential));
        connections.RotateAsync(current.UserId!.Value, credential.Connection.Id, cancellation.Token)
            .Returns(_ => Stage(credential));
        connections.RevokeAsync(current.UserId!.Value, credential.Connection.Id, cancellation.Token)
            .Returns(_ => Stage(Result.Success));

        var pending = HandleAsync(operation, current, connections, transaction, credential.Connection.Id, cancellation.Token);
        try
        {
            var ready = await Task.WhenAny(staged.Task, pending).WaitAsync(cancellation.Token);
            if (ready == pending)
                await pending;
            pending.IsCompleted.ShouldBeFalse();
            transaction.Committed.ShouldBeFalse();
        }
        finally
        {
            commit.TrySetResult();
        }

        var result = await pending;
        result.IsError.ShouldBeFalse();
        if (operation != "revoke")
            result.Value.ShouldBeSameAs(credential);
        transaction.Committed.ShouldBeTrue();
        transaction.Calls.ShouldBe(1);
        return;

        ErrorOr<T> Stage<T>(T value)
        {
            transaction.Active.ShouldBeTrue();
            staged.TrySetResult();
            return value;
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rotate")]
    [InlineData("revoke")]
    public async Task CredentialMutation_Unauthenticated_DoesNotOpenTransaction(string operation)
    {
        var connections = Substitute.For<IVfsConnections>();
        var transaction = new TestTransaction();

        var result = await HandleAsync(operation, Substitute.For<ICurrentUser>(), connections, transaction, Guid.NewGuid());

        result.FirstError.ShouldBe(ConsumerErrors.CurrentUserRequired());
        transaction.Calls.ShouldBe(0);
        connections.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rotate")]
    [InlineData("revoke")]
    public async Task CredentialMutation_StoreRejection_DoesNotCommit(string operation)
    {
        var connections = Substitute.For<IVfsConnections>();
        connections.CreateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<VfsFormat>())
            .Returns(VfsConnectionErrors.LimitReached());
        connections.RotateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(VfsConnectionErrors.LimitReached());
        connections.RevokeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(VfsConnectionErrors.NotFound());
        var transaction = new TestTransaction();

        var result = await HandleAsync(operation, CurrentUser(), connections, transaction, Guid.NewGuid());

        result.FirstError.ShouldBe(operation == "revoke" ? VfsConnectionErrors.NotFound() : VfsConnectionErrors.LimitReached());
        transaction.Calls.ShouldBe(1);
        transaction.Committed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid\nname")]
    public async Task Create_InvalidName_DoesNotOpenTransaction(string? name)
    {
        var connections = Substitute.For<IVfsConnections>();
        var transaction = new TestTransaction();

        var result = await new CreateVfsConnectionCommandHandler(CurrentUser(), connections, transaction)
            .HandleAsync(new(name!));

        result.FirstError.ShouldBe(VfsConnectionErrors.InvalidName());
        transaction.Calls.ShouldBe(0);
        connections.ReceivedCalls().ShouldBeEmpty();
    }

    private static ICurrentUser CurrentUser()
    {
        var current = Substitute.For<ICurrentUser>();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(Guid.NewGuid());
        return current;
    }

    private static async Task<ErrorOr<object>> HandleAsync(string operation, ICurrentUser current, IVfsConnections connections,
        IConsumerSecurityTransaction transaction, Guid id, CancellationToken ct = default) => operation switch
        {
            "create" => Box(await new CreateVfsConnectionCommandHandler(current, connections, transaction).HandleAsync(new("Device"), ct)),
            "rotate" => Box(await new RotateVfsConnectionCommandHandler(current, connections, transaction).HandleAsync(new(id), ct)),
            "revoke" => Box(await new RevokeVfsConnectionCommandHandler(current, connections, transaction).HandleAsync(new(id), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static ErrorOr<object> Box<T>(ErrorOr<T> result)
    {
        if (result.IsError)
            return result.Errors;
        return (object)result.Value!;
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

    private static VfsConnectionDto Dto(VfsFormat format) => new(Guid.NewGuid(), "Device", "test-user", "Active",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, VfsFormats.ToWire(format));
}
