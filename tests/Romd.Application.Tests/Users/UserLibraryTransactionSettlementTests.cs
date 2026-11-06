using ErrorOr;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.CreateLibrary;
using Romd.Admin.Application.Libraries.Commands.DeleteLibrary;
using Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;
using Romd.Admin.Application.Libraries.Commands.UpdateLibrary;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Commands.AssignDefaultLibraryToUsers;
using Romd.Admin.Application.Users.Commands.AssignUserLibrary;
using Romd.Admin.Application.Users.Commands.AssignUserRole;
using Romd.Admin.Application.Users.Commands.CreateUser;
using Romd.Admin.Application.Users.Commands.DeleteUser;
using Romd.Admin.Application.Users.Commands.UpdateUser;
using Romd.Application.Common.Security;
using Romd.Application.Tests.Content;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Users;

public sealed class UserLibraryTransactionSettlementTests
{
    public static IEnumerable<object[]> FailureCases =>
        from owner in new[] { "create-user", "update-user", "delete-user", "role", "assign", "default", "create-library", "update-library", "delete-library", "force" }
        from phase in new[] { "body-oce", "body-io", "commit-oce", "commit-io", "ack" }
        select new object[] { owner, phase };

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task Owner_SecondaryDisposalFailurePreservesPrimaryClassificationOrAcknowledgedSuccess(string owner, string phase)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Exception primary = phase.EndsWith("oce", StringComparison.Ordinal)
            ? new OperationCanceledException("Original interruption", cancellation.Token) : new IOException("Original persistence failure");
        if (phase.StartsWith("body", StringComparison.Ordinal))
            fixture.BodyFailure = primary;
        if (phase.StartsWith("commit", StringComparison.Ordinal))
            fixture.Work.CommitFailure = primary;
        fixture.Work.DisposeFailure = primary is OperationCanceledException ? new IOException("Secondary disposal")
            : new OperationCanceledException("Secondary cleanup cancellation", new CancellationToken(true));
        (bool Error, string? Code) result = default;
        Exception? observed = null;

        try
        { result = await fixture.ExecuteAsync(owner, cancellation.Token); }
        catch (Exception error) { observed = error; }

        if (primary is OperationCanceledException)
        {
            observed.ShouldBeSameAs(primary);
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
            fixture.Logs.ShouldNotContain(entry => entry.Level == LogLevel.Error);
        }
        else
        {
            observed.ShouldBeNull();
            result.Error.ShouldBe(phase != "ack");
            if (phase != "ack")
            {
                result.Code.ShouldBe(owner.EndsWith("library", StringComparison.Ordinal) || owner == "force"
                    ? "Libraries.PersistenceFailed" : "Users.PersistenceFailed");
                fixture.Logs.ShouldContain(entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Error, primary));
                fixture.Logs.ShouldNotContain(entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Error, fixture.Work.DisposeFailure));
            }
        }
        fixture.Work.Commits.ShouldBe(phase.StartsWith("body", StringComparison.Ordinal) ? 0 : 1);
        fixture.Work.Disposals.ShouldBe(1);
        await fixture.Enqueuer.Received(owner == "force" && phase == "ack" ? 1 : 0).EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Force_ExistingPendingJobNeverCommitsAndRetainsExistingAccelerationBoundary(bool cleanupFails)
    {
        var fixture = new Fixture();
        var existing = MaterializationJob.Create(7, "Library", DateTimeOffset.UnixEpoch);
        fixture.Jobs.GetActiveForLibraryAsync(7, Arg.Any<CancellationToken>()).Returns(existing);
        if (cleanupFails)
            fixture.Work.DisposeFailure = new IOException("Read-only transaction cleanup");

        var result = await fixture.ExecuteAsync("force", default);

        result.Error.ShouldBeFalse();
        fixture.Work.Commits.ShouldBe(0);
        fixture.Work.Flushes.ShouldBe(0);
        fixture.Work.Disposals.ShouldBe(1);
        await fixture.Jobs.DidNotReceiveWithAnyArgs().AddStagedAsync(default!, default);
        await fixture.Enqueuer.Received(cleanupFails ? 0 : 1).EnqueueAsync(existing.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task User_BusinessRejectionWithSoleCleanupFailureRetainsExistingPersistenceTranslation()
    {
        var fixture = new Fixture();
        fixture.Users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        fixture.Work.DisposeFailure = new IOException("Sole disposal failure");

        var result = await fixture.ExecuteAsync("role", default);

        result.Code.ShouldBe("Users.PersistenceFailed");
        fixture.Logs.ShouldContain(entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Error, fixture.Work.DisposeFailure));
        fixture.Work.Commits.ShouldBe(0);
    }

    private sealed class Fixture
    {
        private readonly ManagedUser _user = new(Guid.NewGuid(), "user@example.com", ["User"], null, DateTimeOffset.UnixEpoch, null);
        private readonly Library _library = Library.CreateNew("Library", new LibraryConfiguration(), DateTimeOffset.UnixEpoch);
        public IUserAdministration Users { get; } = Substitute.For<IUserAdministration>();
        public ILibraryRepository Libraries { get; } = Substitute.For<ILibraryRepository>();
        public IMaterializationJobRepository Jobs { get; } = Substitute.For<IMaterializationJobRepository>();
        public IMaterializationJobEnqueuer Enqueuer { get; } = Substitute.For<IMaterializationJobEnqueuer>();
        private IAdminEventOutbox Outbox { get; } = Substitute.For<IAdminEventOutbox>();
        private ICurrentUser Current { get; } = Substitute.For<ICurrentUser>();
        public Work Work { get; } = new();
        public Exception? BodyFailure { get; set; }
        public List<(LogLevel Level, Exception? Error)> Logs { get; } = [];

        public Fixture()
        {
            typeof(Library).GetProperty(nameof(Library.Id))!.SetValue(_library, 7);
            Current.UserId.Returns(Guid.NewGuid());
            Users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Read<ManagedUser?>(null));
            Users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => Read<ManagedUser?>(_user));
            Users.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(_user));
            Users.AddRoleAsync(Arg.Any<Guid>(), Arg.Any<RomdRoleType>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(_user));
            Users.UpdateAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(_user));
            Users.ReplaceRoleAsync(Arg.Any<Guid>(), Arg.Any<RomdRoleType>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(_user));
            Users.AssignLibraryAsync(Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(_user));
            Users.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Deleted);
            Libraries.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => Read<Library?>(_library));
            Libraries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(_ => Read<Library?>(_library));
            Libraries.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_library);
            Libraries.ValidateConfigurationReferencesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>(), Arg.Any<int?>()).Returns(_ => Read<string?>(null));
            Libraries.DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
            Libraries.FlagForRematerializationAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_library);
            Jobs.GetActiveForLibraryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((MaterializationJob?)null);
        }
        private T Read<T>(T value) { if (BodyFailure is not null) throw BodyFailure; return value; }
        private CapturedLogger<T> Logger<T>() => new(Logs);
        public async Task<(bool Error, string? Code)> ExecuteAsync(string owner, CancellationToken ct)
        {
            return owner switch
            {
                "create-user" => Outcome(await new CreateUserCommandHandler(Users, Libraries, Work, TimeProvider.System, Logger<CreateUserCommandHandler>()).HandleAsync(new("new@example.com", "Password123", "User", null), ct)),
                "update-user" => Outcome(await new UpdateUserCommandHandler(Users, Work, TimeProvider.System, Logger<UpdateUserCommandHandler>()).HandleAsync(new(_user.Id, null, null), ct)),
                "delete-user" => Outcome(await new DeleteUserCommandHandler(Users, Current, Work, Logger<DeleteUserCommandHandler>()).HandleAsync(new(_user.Id), ct)),
                "role" => Outcome(await new AssignUserRoleCommandHandler(Users, Work, TimeProvider.System, Logger<AssignUserRoleCommandHandler>()).HandleAsync(new(_user.Id, "Manager"), ct)),
                "assign" => Outcome(await new AssignUserLibraryCommandHandler(Users, Libraries, Work, TimeProvider.System, Logger<AssignUserLibraryCommandHandler>()).HandleAsync(new(_user.Id, null), ct)),
                "default" => Outcome(await new AssignDefaultLibraryToUsersCommandHandler(Users, Libraries, Work, TimeProvider.System, Logger<AssignDefaultLibraryToUsersCommandHandler>()).HandleAsync(new([_user.Id]), ct)),
                "create-library" => Outcome(await new CreateLibraryCommandHandler(TestSystemCatalog.Create(), Libraries, Outbox, Work, Logger<CreateLibraryCommandHandler>(), TestDeliveryPolicies.Production, TimeProvider.System).HandleAsync(new("Library", new(), false), ct)),
                "update-library" => Outcome(await new UpdateLibraryCommandHandler(TestSystemCatalog.Create(), Libraries, Outbox, Work, Logger<UpdateLibraryCommandHandler>(), TestDeliveryPolicies.Production, TimeProvider.System).HandleAsync(new(7, "Changed", null, null), ct)),
                "delete-library" => Outcome(await new DeleteLibraryCommandHandler(Libraries, Outbox, Work, Logger<DeleteLibraryCommandHandler>()).HandleAsync(new(7), ct)),
                "force" => Outcome(await new ForceMaterializeLibraryCommandHandler(Libraries, Jobs, Enqueuer, Outbox, Current, Work, Logger<ForceMaterializeLibraryCommandHandler>(), TimeProvider.System).HandleAsync(new(7), ct)),
                _ => throw new ArgumentOutOfRangeException(nameof(owner))
            };
        }
        private static (bool Error, string? Code) Outcome<T>(ErrorOr<T> result) => (result.IsError, result.IsError ? result.FirstError.Code : null);
    }

    private sealed class Work : IUnitOfWork, ITransaction
    {
        public Exception? CommitFailure { get; set; }
        public Exception? DisposeFailure { get; set; }
        public int Commits { get; private set; }
        public int Disposals { get; private set; }
        public int Flushes { get; private set; }
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.FromResult<ITransaction>(this);
        public Task FlushAsync(CancellationToken cancellationToken = default) { Flushes++; return Task.CompletedTask; }
        public Task CommitAsync(CancellationToken cancellationToken = default) { Commits++; if (CommitFailure is not null) throw CommitFailure; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Disposal settles uncommitted work");
        public ValueTask DisposeAsync() { Disposals++; if (DisposeFailure is not null) throw DisposeFailure; return ValueTask.CompletedTask; }
        public TransactionConflict ConflictOf(Exception error) => throw new InvalidOperationException("No automatic retry");
    }

    private sealed class CapturedLogger<T>(List<(LogLevel Level, Exception? Error)> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => entries.Add((logLevel, exception));
    }
}
