using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Commands.AssignDefaultLibraryToUsers;
using Romd.Admin.Application.Users.Commands.AssignUserLibrary;
using Romd.Admin.Application.Users.Commands.AssignUserRole;
using Romd.Admin.Application.Users.Commands.CreateUser;
using Romd.Admin.Application.Users.Commands.DeleteUser;
using Romd.Admin.Application.Users.Commands.UpdateUser;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Users;

public sealed class UserCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly ManagedUser Existing = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "user@example.com",
        ["User"],
        null,
        Now.AddDays(-1),
        null);

    [Fact]
    public async Task Create_MissingFields_PrecedesInvalidRole()
    {
        var fixture = new Fixture();
        var handler = fixture.CreateHandler();

        var result = await handler.HandleAsync(new CreateUserCommand("", "", "nope", "bad"));

        result.FirstError.Code.ShouldBe("Users.MissingRequiredFields");
        fixture.UnitOfWork.BeginCount.ShouldBe(0);
        await fixture.Users.DidNotReceiveWithAnyArgs().GetByEmailAsync(default!, default);
    }

    [Fact]
    public async Task Create_InvalidRole_PrecedesDuplicateAndLibraryResolution()
    {
        var fixture = new Fixture();
        var handler = fixture.CreateHandler();

        var result = await handler.HandleAsync(new CreateUserCommand("user@example.com", "Password123", "nope", "bad"));

        result.FirstError.Code.ShouldBe("Users.InvalidRole");
        await fixture.Users.DidNotReceiveWithAnyArgs().GetByEmailAsync(default!, default);
        await fixture.Libraries.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        fixture.UnitOfWork.BeginCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    public async Task Create_NumericRole_ReturnsInvalidRoleWithoutStartingTransaction(string role)
    {
        var fixture = new Fixture();
        var handler = fixture.CreateHandler();

        var result = await handler.HandleAsync(
            new CreateUserCommand("user@example.com", "Password123", role, null));

        result.FirstError.Code.ShouldBe("Users.InvalidRole");
        fixture.UnitOfWork.BeginCount.ShouldBe(0);
        await fixture.Users.DidNotReceiveWithAnyArgs().GetByEmailAsync(default!, default);
    }

    [Fact]
    public async Task Create_CaseInsensitiveNamedRole_AssignsParsedRole()
    {
        var fixture = new Fixture();
        fixture.Users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        fixture.Libraries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns((Library?)null);
        fixture.Users.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), null, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        fixture.Users.AddRoleAsync(Existing.Id, RomdRoleType.Manager, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing with { Roles = ["Manager"] }));
        var handler = fixture.CreateHandler();

        var result = await handler.HandleAsync(
            new CreateUserCommand("user@example.com", "Password123", "mAnAgEr", null));

        result.IsError.ShouldBeFalse();
        await fixture.Users.Received(1)
            .AddRoleAsync(Existing.Id, RomdRoleType.Manager, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_DuplicateEmail_PrecedesRequestedLibraryResolution()
    {
        var fixture = new Fixture();
        fixture.Users.GetByEmailAsync("user@example.com", Arg.Any<CancellationToken>()).Returns(Existing);
        var handler = fixture.CreateHandler();

        var result = await handler.HandleAsync(new CreateUserCommand("user@example.com", "Password123", "User", "bad"));

        result.FirstError.Code.ShouldBe("Users.DuplicateEmail");
        await fixture.Libraries.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(0);
        fixture.UnitOfWork.Transaction.RollbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task Create_Success_AssignsRoleAndCommitsExactlyOnce()
    {
        var fixture = new Fixture();
        fixture.Users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        fixture.Libraries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns((Library?)null);
        fixture.Users.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), null, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        var withRole = Existing with { Roles = ["Manager"] };
        fixture.Users.AddRoleAsync(Existing.Id, RomdRoleType.Manager, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(withRole));
        var handler = fixture.CreateHandler();

        var result = await handler.HandleAsync(new CreateUserCommand("user@example.com", "Password123", "Manager", null));

        result.IsError.ShouldBeFalse();
        result.Value.Roles.ShouldBe(["Manager"]);
        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(1);
        fixture.UnitOfWork.Transaction.RollbackCount.ShouldBe(0);
    }

    [Fact]
    public async Task Create_RoleFailure_AbandonsTransactionWithoutCommit()
    {
        var fixture = new Fixture();
        fixture.Users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        fixture.Libraries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns((Library?)null);
        fixture.Users.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), null, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        fixture.Users.AddRoleAsync(Existing.Id, RomdRoleType.User, Arg.Any<CancellationToken>())
            .Returns(UserErrors.AssignRoleFailed());
        var handler = fixture.CreateHandler();

        var result = await handler.HandleAsync(new CreateUserCommand("user@example.com", "Password123", "User", null));

        result.FirstError.Code.ShouldBe("Users.AssignRoleFailed");
        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(0);
        fixture.UnitOfWork.Transaction.RollbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task AssignRole_MissingUser_PrecedesInvalidRole()
    {
        var fixture = new Fixture();
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        var handler = fixture.AssignRoleHandler();

        var result = await handler.HandleAsync(new AssignUserRoleCommand(Existing.Id, "invalid"));

        result.FirstError.Code.ShouldBe("Users.NotFound");
        await fixture.Users.DidNotReceiveWithAnyArgs().ReplaceRoleAsync(default, default, default, default);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    public async Task AssignRole_NumericRole_ReturnsInvalidRoleAfterUserLookup(string role)
    {
        var fixture = new Fixture();
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(Existing);
        var handler = fixture.AssignRoleHandler();

        var result = await handler.HandleAsync(new AssignUserRoleCommand(Existing.Id, role));

        result.FirstError.Code.ShouldBe("Users.InvalidRole");
        await fixture.Users.Received(1).GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>());
        await fixture.Users.DidNotReceiveWithAnyArgs().ReplaceRoleAsync(default, default, default, default);
        fixture.UnitOfWork.Transaction.RollbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task AssignRole_CaseInsensitiveNamedRole_AssignsParsedRole()
    {
        var fixture = new Fixture();
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(Existing);
        fixture.Users.ReplaceRoleAsync(Existing.Id, RomdRoleType.Admin, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing with { Roles = ["Admin"] }));
        var handler = fixture.AssignRoleHandler();

        var result = await handler.HandleAsync(new AssignUserRoleCommand(Existing.Id, "aDmIn"));

        result.IsError.ShouldBeFalse();
        await fixture.Users.Received(1)
            .ReplaceRoleAsync(Existing.Id, RomdRoleType.Admin, Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignLibrary_MissingUser_PrecedesInvalidLibraryId()
    {
        var fixture = new Fixture();
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        var handler = fixture.AssignLibraryHandler();

        var result = await handler.HandleAsync(new AssignUserLibraryCommand(Existing.Id, "invalid"));

        result.FirstError.Code.ShouldBe("Users.NotFound");
        await fixture.Libraries.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Delete_FixedOrCurrentUser_ProtectsBeforeLookup(bool fixedSystemId, bool currentUser)
    {
        var fixture = new Fixture();
        Guid target = fixedSystemId ? SystemActor.UserId : Existing.Id;
        fixture.CurrentUser.UserId.Returns(currentUser ? target : Guid.NewGuid());
        var handler = fixture.DeleteHandler();

        var result = await handler.HandleAsync(new DeleteUserCommand(target));

        result.FirstError.Code.ShouldBe("Users.ProtectedUser");
        fixture.UnitOfWork.BeginCount.ShouldBe(0);
        await fixture.Users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Fact]
    public async Task Delete_SystemEmail_ProtectsAfterLookupWithoutDeleting()
    {
        var fixture = new Fixture();
        fixture.CurrentUser.UserId.Returns(Guid.NewGuid());
        var protectedUser = Existing with { Email = SystemActor.Email };
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(protectedUser);
        var handler = fixture.DeleteHandler();

        var result = await handler.HandleAsync(new DeleteUserCommand(Existing.Id));

        result.FirstError.Code.ShouldBe("Users.ProtectedUser");
        await fixture.Users.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(0);
        fixture.UnitOfWork.Transaction.RollbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task AssignDefault_NoDefaultLibrary_DoesNotMutateOrCommit()
    {
        var fixture = new Fixture();
        fixture.Libraries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns((Library?)null);
        var handler = fixture.AssignDefaultHandler();

        var result = await handler.HandleAsync(new AssignDefaultLibraryToUsersCommand([Existing.Id]));

        result.FirstError.Code.ShouldBe("Users.NoDefaultLibrary");
        await fixture.Users.DidNotReceiveWithAnyArgs().AssignDefaultLibraryAsync(default, default, default);
        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(0);
    }

    [Fact]
    public async Task Update_DisposeFailureAfterCommit_ReturnsDurableSuccess()
    {
        var fixture = new Fixture(throwOnDisposeAfterCommit: true);
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(Existing);
        var updated = Existing with { Email = "new@example.com", UpdatedAt = Now };
        fixture.Users.UpdateAsync(Existing.Id, updated.Email, null, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(updated));
        var handler = fixture.UpdateHandler();

        var result = await handler.HandleAsync(new UpdateUserCommand(Existing.Id, updated.Email, null));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(updated);
        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("AssignRole")]
    [InlineData("AssignLibrary")]
    [InlineData("AssignDefault")]
    public async Task Mutation_DisposeFailureAfterCommit_NeverReversesDurableSuccess(string mutation)
    {
        var fixture = new Fixture(throwOnDisposeAfterCommit: true);
        fixture.CurrentUser.UserId.Returns(Guid.NewGuid());
        fixture.Users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ManagedUser?)null);
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(Existing);
        fixture.Users.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), null, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        fixture.Users.AddRoleAsync(Existing.Id, RomdRoleType.User, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        fixture.Users.UpdateAsync(Existing.Id, null, null, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        fixture.Users.ReplaceRoleAsync(Existing.Id, RomdRoleType.Manager, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        fixture.Users.AssignLibraryAsync(Existing.Id, null, Now, Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(Existing));
        fixture.Users.DeleteAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(Result.Deleted);

        bool succeeded = mutation switch
        {
            "Create" => !(await fixture.CreateHandler().HandleAsync(
                new CreateUserCommand("user@example.com", "Password123", "User", null))).IsError,
            "Update" => !(await fixture.UpdateHandler().HandleAsync(
                new UpdateUserCommand(Existing.Id, null, null))).IsError,
            "Delete" => !(await fixture.DeleteHandler().HandleAsync(
                new DeleteUserCommand(Existing.Id))).IsError,
            "AssignRole" => !(await fixture.AssignRoleHandler().HandleAsync(
                new AssignUserRoleCommand(Existing.Id, "Manager"))).IsError,
            "AssignLibrary" => !(await fixture.AssignLibraryHandler().HandleAsync(
                new AssignUserLibraryCommand(Existing.Id, null))).IsError,
            _ => await AssignDefaultAsync(fixture)
        };

        succeeded.ShouldBeTrue();
        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(1);
    }

    [Fact]
    public async Task Update_PrecommitCancellation_PropagatesAndRollsBack()
    {
        var fixture = new Fixture();
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(Existing);
        fixture.Users.UpdateAsync(Existing.Id, null, "Password123", Now, Arg.Any<CancellationToken>())
            .Returns<Task<ErrorOr<ManagedUser>>>(_ => throw new OperationCanceledException());
        var handler = fixture.UpdateHandler();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new UpdateUserCommand(Existing.Id, null, "Password123")));

        fixture.UnitOfWork.Transaction.CommitCount.ShouldBe(0);
        fixture.UnitOfWork.Transaction.RollbackCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("role")]
    [InlineData("library")]
    public async Task Mutation_SystemActor_IsRejectedBeforeTransaction(string action)
    {
        var fixture = new Fixture();
        var result = action switch
        {
            "email" => await fixture.UpdateHandler().HandleAsync(new(SystemActor.UserId, "changed@example.com", null)),
            "role" => await fixture.AssignRoleHandler().HandleAsync(new(SystemActor.UserId, "Admin")),
            _ => await fixture.AssignLibraryHandler().HandleAsync(new(SystemActor.UserId, null))
        };
        result.FirstError.Code.ShouldBe("Users.ProtectedUser");
        fixture.UnitOfWork.BeginCount.ShouldBe(0);
    }

    [Fact]
    public async Task AssignDefault_MissingSelection_IsRejectedBeforeTransaction()
    {
        var fixture = new Fixture();
        var result = await fixture.AssignDefaultHandler().HandleAsync(new());
        result.FirstError.Code.ShouldBe("Users.SelectionRequired");
        fixture.UnitOfWork.BeginCount.ShouldBe(0);
    }

    private sealed class Fixture
    {
        public Fixture(bool throwOnDisposeAfterCommit = false)
        {
            UnitOfWork = new RecordingUnitOfWork(throwOnDisposeAfterCommit);
            Users = Substitute.For<IUserAdministration>();
            Libraries = Substitute.For<ILibraryRepository>();
            CurrentUser = Substitute.For<ICurrentUser>();
        }

        public RecordingUnitOfWork UnitOfWork { get; }
        public IUserAdministration Users { get; }
        public ILibraryRepository Libraries { get; }
        public ICurrentUser CurrentUser { get; }

        public CreateUserCommandHandler CreateHandler() =>
            new(Users, Libraries, UnitOfWork, new FixedTimeProvider(Now), NullLogger<CreateUserCommandHandler>.Instance);

        public UpdateUserCommandHandler UpdateHandler() =>
            new(Users, UnitOfWork, new FixedTimeProvider(Now), NullLogger<UpdateUserCommandHandler>.Instance);

        public AssignUserRoleCommandHandler AssignRoleHandler() =>
            new(Users, UnitOfWork, new FixedTimeProvider(Now), NullLogger<AssignUserRoleCommandHandler>.Instance);

        public AssignUserLibraryCommandHandler AssignLibraryHandler() =>
            new(Users, Libraries, UnitOfWork, new FixedTimeProvider(Now), NullLogger<AssignUserLibraryCommandHandler>.Instance);

        public AssignDefaultLibraryToUsersCommandHandler AssignDefaultHandler() =>
            new(Users, Libraries, UnitOfWork, new FixedTimeProvider(Now), NullLogger<AssignDefaultLibraryToUsersCommandHandler>.Instance);

        public DeleteUserCommandHandler DeleteHandler() =>
            new(Users, CurrentUser, UnitOfWork, NullLogger<DeleteUserCommandHandler>.Instance);
    }

    private static async Task<bool> AssignDefaultAsync(Fixture fixture)
    {
        var library = Library.CreateNew("Default", new LibraryConfiguration());
        typeof(Library).GetProperty(nameof(Library.Id))!.SetValue(library, 7);
        library.MarkAsDefault();
        fixture.Libraries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(library);
        fixture.Users.GetByIdAsync(Existing.Id, Arg.Any<CancellationToken>()).Returns(Existing with { LibraryId = null });
        fixture.Users.AssignLibraryAsync(Existing.Id, 7, Now, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(Existing with { LibraryId = 7 }));
        return !(await fixture.AssignDefaultHandler().HandleAsync(new AssignDefaultLibraryToUsersCommand([Existing.Id]))).IsError;
    }

    private sealed class RecordingUnitOfWork(bool throwOnDisposeAfterCommit) : IUnitOfWork
    {
        public RecordingTransaction Transaction { get; } = new(throwOnDisposeAfterCommit);
        public int BeginCount { get; private set; }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            BeginCount++;
            return Task.FromResult<ITransaction>(Transaction);
        }
    }

    private sealed class RecordingTransaction(bool throwOnDisposeAfterCommit) : ITransaction
    {
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (CommitCount == 0)
            {
                RollbackCount++;
            }
            else if (throwOnDisposeAfterCommit)
            {
                throw new InvalidOperationException("Injected cleanup failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
